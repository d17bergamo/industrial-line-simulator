using LineSimulator.Models;

namespace LineSimulator.Engine.Runtime;

/// <summary>Profilo di posizionamento a encoder: v = min(vmax, √(2·0.95·d·r), r/dt).</summary>
public static class PositioningProfile
{
    public const double BrakingMargin = 0.95;

    public static double Command(double remainingM, double vmax, double decel, double dt)
    {
        if (remainingM <= 0 || vmax <= 0) return 0;
        double vBrake = Math.Sqrt(2 * BrakingMargin * Math.Max(1e-6, decel) * remainingM);
        return Math.Min(vmax, Math.Min(vBrake, remainingM / dt));
    }
}

/// <summary>Stati funzionali della legatrice KNCA 7/800 (macchina di processo, distinta dal ciclo testa).</summary>
public enum StrapperProcessState
{
    /// <summary>Disponibile: nessun pacco in stazione o in arrivo.</summary>
    Available,
    /// <summary>Attesa pacco: pacco in arrivo o in stazione ma non ancora alla fermata di questa legatrice.</summary>
    WaitingPackage,
    /// <summary>Posizionamento del pacco in corso.</summary>
    Positioning,
    /// <summary>Pacco in posizione (fermo, compattazione in corso).</summary>
    PackageInPosition,
    /// <summary>Attesa consenso: stringifascio chiuso e confermato / compattatore.</summary>
    WaitingClampConsent,
    /// <summary>Ciclo di legatura (preparazione, legatura, ritorno testa).</summary>
    StrappingCycle,
    /// <summary>Ciclo completato: attesa apertura dispositivi o delle altre legatrici.</summary>
    CycleCompleted,
    /// <summary>Scarico pacco dalla stazione.</summary>
    Unloading,
    /// <summary>Guasto o blocco (in attesa di ripristino).</summary>
    Fault
}

public enum ClampState
{
    Open,
    Closing,
    Closed,
    Opening,
    Fault
}

/// <summary>
/// Dispositivo a chiusura con conferma (stringifascio KNOF, compattatore laterale):
/// comando → movimento → segnale di conferma (ritardo) oppure timeout → guasto → ripristino → nuovo comando.
/// </summary>
public sealed class ClampRuntime
{
    private bool _faultPending;
    private bool _justCommanded;

    public ClampRuntime(SequenceDeviceConfig config, double closeS, double openS, double holdS)
    {
        Config = config;
        CloseS = Math.Max(0, closeS);
        OpenS = Math.Max(0, openS);
        HoldS = Math.Max(0, holdS);
    }

    public SequenceDeviceConfig Config { get; }
    public string Id => Config.Id;
    public double CloseS { get; }
    public double OpenS { get; }
    public double HoldS { get; }
    public ClampState State { get; private set; } = ClampState.Open;
    public double ElapsedS { get; private set; }
    public int Cycles { get; private set; }
    public int Faults { get; private set; }
    public double BusyTimeS { get; internal set; }
    public bool IsClosed => State == ClampState.Closed;
    public bool IsOpen => State == ClampState.Open;

    /// <summary>Frazione di chiusura per la grafica (0 aperto, 1 chiuso).</summary>
    public double ClosedFraction => State switch
    {
        ClampState.Closing => CloseS > 0 ? Math.Min(1, ElapsedS / CloseS) : 1,
        ClampState.Closed => 1,
        ClampState.Opening => OpenS > 0 ? Math.Max(0, 1 - ElapsedS / OpenS) : 0,
        ClampState.Fault => 0.5,
        _ => 0
    };

    internal bool CommandClose()
    {
        if (State != ClampState.Open) return false;
        State = ClampState.Closing;
        ElapsedS = 0;
        _justCommanded = true;
        Cycles++;
        _faultPending = Config.SimulatedFaultAtCycle > 0 && Cycles == Config.SimulatedFaultAtCycle;
        return true;
    }

    internal bool CommandOpen()
    {
        if (State != ClampState.Closed) return false;
        State = ClampState.Opening;
        ElapsedS = 0;
        _justCommanded = true;
        return true;
    }

    /// <summary>Avanza la sequenza; restituisce il messaggio dell'eventuale transizione (Alarm = allarme).</summary>
    internal (string Text, bool Alarm)? Advance(double dt, bool hold)
    {
        if (State is ClampState.Open or ClampState.Closed || hold) return null;
        // Il movimento inizia alla scansione successiva al comando (come un'uscita PLC)
        if (_justCommanded) { _justCommanded = false; return null; }
        ElapsedS += dt;
        switch (State)
        {
            case ClampState.Closing:
                if (!_faultPending && ElapsedS >= CloseS + Config.ConfirmDelayS - 1e-9)
                {
                    State = ClampState.Closed; ElapsedS = 0;
                    return ("chiuso, conferma di chiusura ricevuta", false);
                }
                if (ElapsedS >= Config.ConfirmTimeoutS - 1e-9)
                {
                    State = ClampState.Fault; ElapsedS = 0; Faults++;
                    return ($"ALLARME: conferma di chiusura non ricevuta entro {Config.ConfirmTimeoutS:0.#} s (guasto)", true);
                }
                return null;
            case ClampState.Fault:
                if (ElapsedS >= Config.FaultResetTimeS - 1e-9)
                {
                    _faultPending = false; State = ClampState.Closing; ElapsedS = 0;
                    return ("guasto ripristinato: nuovo comando di chiusura", false);
                }
                return null;
            case ClampState.Opening:
                if (ElapsedS >= OpenS + Config.ConfirmDelayS - 1e-9)
                {
                    State = ClampState.Open; ElapsedS = 0;
                    return ("aperto, conferma di apertura ricevuta", false);
                }
                return null;
        }
        return null;
    }
}

public enum RejectorState
{
    Idle,
    Enabled,
    Positioning,
    InPosition,
    Ejecting,
    Returning,
    RestartConsent,
    Fault
}

/// <summary>Rigettatrice SMA: stato della sequenza di rigetto.</summary>
public sealed class RejectorRuntime
{
    public RejectorRuntime(RejectorConfig config, ZoneRuntime zone, IReadOnlyCollection<int> numbers)
    {
        Config = config;
        Zone = zone;
        StartM = config.PositionMm / 1000.0;
        EndM = (config.PositionMm + config.LengthMm) / 1000.0;
        ToleranceM = Math.Max(0.0005, config.PositionToleranceMm / 1000.0);
        StrokeMaxM = Math.Max(0, config.StrokeMm / 1000.0);
        RejectNumbers = numbers.ToHashSet();
    }

    public RejectorConfig Config { get; }
    public ZoneRuntime Zone { get; }
    public string Id => Config.Id;
    public double StartM { get; }
    public double EndM { get; }
    public double CenterM => (StartM + EndM) / 2;
    public double ToleranceM { get; }
    public double StrokeMaxM { get; }
    public HashSet<int> RejectNumbers { get; }

    public RejectorState State { get; internal set; } = RejectorState.Idle;
    public PackageRuntime? Package { get; internal set; }
    public double TargetHeadM { get; internal set; }
    public double ElapsedS { get; internal set; }
    public double StrokeM { get; internal set; }
    public int Cycles { get; internal set; }
    public int Rejected { get; internal set; }
    public int Faults { get; internal set; }
    public double BusyTimeS { get; internal set; }
    internal bool FaultPending;
    public bool Busy => State != RejectorState.Idle;

    public bool ShouldReject(PackageRuntime p) => RejectNumbers.Contains(p.Sequence) || Config.RejectTypeIds.Contains(p.Type.Id);

    public double VelocityCommand(double dt) => State == RejectorState.Positioning && Package != null
        ? PositioningProfile.Command(TargetHeadM - Package.HeadM, Zone.Config.HighSpeed, Zone.Config.Deceleration, dt)
        : 0;
}

public enum ScaleState
{
    Idle,
    Directed,
    Positioning,
    PositionCheck,
    Stabilizing,
    Acquiring,
    Completed,
    Releasing,
    Fault
}

/// <summary>Bilico di pesatura. Un pacco più lungo di un bilico viene pesato da più bilici (pesatura combinata).</summary>
public sealed class ScaleRuntime
{
    public ScaleRuntime(WeighingScaleConfig config, int index)
    {
        Config = config;
        Index = index;
        StartM = config.StartMm / 1000.0;
        EndM = config.EndMm / 1000.0;
        ToleranceM = Math.Max(0.0005, config.PositionToleranceMm / 1000.0);
    }

    public WeighingScaleConfig Config { get; }
    public int Index { get; }
    public string Id => Config.Id;
    public double StartM { get; }
    public double EndM { get; }
    public double ToleranceM { get; }

    public ScaleState State { get; internal set; } = ScaleState.Idle;
    public PackageRuntime? Package { get; internal set; }
    /// <summary>Bilico capofila in pesatura combinata (null se capofila o libero).</summary>
    public ScaleRuntime? Lead { get; internal set; }
    internal readonly List<ScaleRuntime> Followers = new();
    public double TargetHeadM { get; internal set; }
    public double Speed { get; internal set; }
    public double ElapsedS { get; internal set; }
    public double? LastMeasuredKg { get; internal set; }
    public int Weighings { get; internal set; }
    public int Cycles { get; internal set; }
    public int Faults { get; internal set; }
    public double BusyTimeS { get; internal set; }
    public bool ShortPosition { get; internal set; }
    internal bool FaultPending;
    public bool Busy => State != ScaleState.Idle;
    public bool Combined => Followers.Count > 0;
}

public enum StopState
{
    Free,
    Assigned,
    Positioning,
    /// <summary>Intestatura di coda: pacco oltre il fermo, fermo sollevato, ritorno indietro fino a battere di coda.</summary>
    Returning,
    Aligning,
    Aligned
}

/// <summary>Fermo intermedio: assegnazione del pacco, posizionamento, intestatura di testa o di coda.</summary>
public sealed class StopRuntime
{
    public StopRuntime(IntermediateStopConfig config, StopReference reference)
    {
        Config = config;
        Reference = reference;
        PositionM = config.PositionMm / 1000.0;
        ToleranceM = Math.Max(0.0005, config.PositionToleranceMm / 1000.0);
        OvershootM = reference == StopReference.Tail ? Math.Max(0.02, config.TailOvershootMm / 1000.0) : 0;
        ReturnSpeed = Math.Max(0.01, config.TailReturnSpeed);
    }

    public IntermediateStopConfig Config { get; }
    public string Id => Config.Id;
    public double PositionM { get; }
    public double ToleranceM { get; }
    /// <summary>Intestatura applicata (dalle impostazioni della simulazione).</summary>
    public StopReference Reference { get; }
    /// <summary>Corsa oltre il fermo prima del ritorno (solo intestatura di coda).</summary>
    public double OvershootM { get; }
    public double ReturnSpeed { get; }
    public StopState State { get; internal set; } = StopState.Free;
    public PackageRuntime? Package { get; internal set; }
    public double TargetHeadM { get; internal set; }
    public double ElapsedS { get; internal set; }
    /// <summary>Fermo sollevato (solo con il pacco assegnato e nessun pacco sopra il fermo).</summary>
    public bool Raised { get; internal set; }
    public int Packages { get; internal set; }
    public double OccupiedTimeS { get; internal set; }
}
