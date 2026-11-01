using LineSimulator.Optimization;

namespace LineSimulator.Engine.Runtime;

public enum StationState
{
    Idle,
    Positioning,
    Strapping,
    Releasing,
    /// <summary>Pacco in posizione: compattatore laterale in chiusura / mantenimento.</summary>
    Compacting,
    /// <summary>Stringifascio KNOF in chiusura, attesa conferma (consenso legatura).</summary>
    Clamping,
    /// <summary>Fine legatura: mantenimento e apertura di stringifascio e compattatore.</summary>
    Unclamping
}

/// <summary>
/// Stazione di legatura: esegue il piano scelto dall'ottimizzatore posizionando il pacco
/// (profilo di velocità con frenatura calcolata sulla distanza residua, come un posizionatore a encoder)
/// e avviando le legatrici previste a ogni fermata.
/// </summary>
public sealed class StationRuntime
{
    public StationRuntime(ZoneRuntime zone, double positioningSpeed, double toleranceM)
    {
        Zone = zone;
        PositioningSpeed = positioningSpeed;
        ToleranceM = toleranceM;
    }

    public ZoneRuntime Zone { get; }
    public double PositioningSpeed { get; }
    public double ToleranceM { get; }

    public StationState State { get; internal set; } = StationState.Idle;
    public PackageRuntime? Package { get; internal set; }
    public StrappingPlan? Plan { get; internal set; }
    public int StopIndex { get; internal set; }
    public double TargetHeadM { get; internal set; }
    internal List<StrapperRuntime> ActiveStrappers { get; } = new();
    /// <summary>Tempo trascorso nella fase corrente (mantenimenti).</summary>
    internal double PhaseElapsedS;
    internal bool StrappingPhaseDone;
    internal bool CompactorOpenCommanded;
    internal bool ClampOpenCommanded;
    internal bool InterlockReported;

    public double BusyTimeS { get; internal set; }
    public double BlockedTimeS { get; internal set; }
    public int PackagesProcessed { get; internal set; }
    public double MaxPositioningErrorM { get; internal set; }

    /// <summary>Fattore di sicurezza sulla decelerazione per garantire che la rampa possa seguire il profilo.</summary>
    public const double BrakingMargin = 0.95;

    /// <summary>Velocità di riferimento in posizionamento: v = min(vmax, √(2·0.95·d·r), r/dt).</summary>
    public double VelocityCommand(double dt)
    {
        if (Package == null || State != StationState.Positioning) return 0;
        double r = TargetHeadM - Package.HeadM;
        if (r <= 0) return 0;
        double vmax = StopIndex == 0 ? Zone.Config.HighSpeed : PositioningSpeed;
        double vBrake = Math.Sqrt(2 * BrakingMargin * Zone.Config.Deceleration * r);
        return Math.Min(vmax, Math.Min(vBrake, r / dt));
    }
}
