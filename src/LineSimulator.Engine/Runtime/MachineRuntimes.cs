using LineSimulator.Models;

namespace LineSimulator.Engine.Runtime;

/// <summary>
/// Fotocellula: stato grezzo dalla posizione reale dei pacchi e uscita ritardata del tempo di risposta
/// (la commutazione avviene solo se lo stato grezzo resta stabile per tutto il tempo di risposta).
/// </summary>
public sealed class PhotocellRuntime
{
    public PhotocellRuntime(PhotocellConfig config)
    {
        Config = config;
        PositionM = config.PositionMm / 1000.0;
        ResponseS = Math.Max(0, config.ResponseTimeMs / 1000.0);
    }

    public PhotocellConfig Config { get; }
    public string Id => Config.Id;
    public double PositionM { get; }
    public double ResponseS { get; }

    public bool Raw { get; private set; }
    public bool Output { get; private set; }
    public PackageRuntime? RawPackage { get; private set; }
    public PackageRuntime? LastPackage { get; private set; }
    private double? _pendingSince;

    public bool IsEntry => Config.IsEntryPhotocell;
    /// <summary>Fronte di salita dell'uscita nella scansione corrente (arrivo testa pacco).</summary>
    public bool RoseThisScan { get; private set; }
    /// <summary>Fronte di discesa dell'uscita nella scansione corrente (uscita completa / passaggio coda).</summary>
    public bool FellThisScan { get; private set; }
    /// <summary>Pacco che ha attivato l'uscita (valido fino al fronte di discesa compreso).</summary>
    public PackageRuntime? OutputPackage { get; private set; }

    /// <summary>Condizione di attivazione soddisfatta secondo logica di rilevamento e configurazione.</summary>
    public bool Active => Config.IsEnabled && Config.DetectionMode switch
    {
        PhotocellDetectionMode.ArrivalPulse => RoseThisScan,
        PhotocellDetectionMode.ClearancePulse => FellThisScan,
        _ => Config.Activation == PhotocellActivation.WhenOccupied ? Output : !Output
    };

    /// <summary>Aggiorna lo stato; restituisce true se l'uscita ha commutato.</summary>
    internal bool Update(double time, PackageRuntime? detected)
    {
        RoseThisScan = FellThisScan = false;
        if (!Config.IsEnabled) { Raw = false; RawPackage = null; bool ch = Output; Output = false; return ch; }
        Raw = detected != null;
        RawPackage = detected;
        if (detected != null) LastPackage = detected;
        if (Raw == Output) { _pendingSince = null; return false; }
        _pendingSince ??= time;
        if (time - _pendingSince.Value >= ResponseS - 1e-9)
        {
            Output = Raw;
            _pendingSince = null;
            if (Output) { RoseThisScan = true; OutputPackage = detected; Arrivals++; }
            else { FellThisScan = true; Passages++; }
            return true;
        }
        return false;
    }

    public int Arrivals { get; private set; }
    public int Passages { get; private set; }

    public double OccupiedTimeS { get; internal set; }
    public int Activations { get; internal set; }
}

public enum StrapperState
{
    Idle,
    Preparing,
    Strapping,
    Returning
}

/// <summary>Legatrice: ciclo preparazione → legatura → ritorno.</summary>
public sealed class StrapperRuntime
{
    public StrapperRuntime(StrapperConfig config, int index)
    {
        Config = config;
        Index = index;
        PositionM = config.PositionMm / 1000.0;
    }

    public StrapperConfig Config { get; }
    public int Index { get; }
    public string Id => Config.Id;
    public double PositionM { get; }
    public StrapperState State { get; private set; } = StrapperState.Idle;
    public double PhaseElapsedS { get; private set; }
    public PackageRuntime? Package { get; private set; }
    /// <summary>Ultimo pacco lavorato (per gli stati di processo KNCA).</summary>
    public PackageRuntime? LastPackage { get; private set; }
    public int StrapIndex { get; private set; } = -1;
    public bool Busy => State != StrapperState.Idle;

    public double BusyTimeS { get; internal set; }
    public int CompletedCycles { get; private set; }
    public int CyclesStarted { get; private set; }

    /// <summary>Stato funzionale KNCA 7/800 calcolato dal motore a ogni scansione.</summary>
    public StrapperProcessState ProcessState { get; internal set; } = StrapperProcessState.Available;
    internal double ProcessStateSinceS;
    internal bool ConsentAlarmRaised;

    /// <summary>Guasto simulato: la testa resta bloccata fino al ripristino, poi il ciclo riparte.</summary>
    public bool Faulted { get; private set; }
    public double FaultElapsedS { get; private set; }
    public int Faults { get; private set; }
    internal bool FaultJustCleared;

    internal void Start(PackageRuntime p, int strapIndex)
    {
        if (Busy) throw new InvalidOperationException($"Legatrice {Id} già occupata.");
        Package = p;
        LastPackage = p;
        StrapIndex = strapIndex;
        State = StrapperState.Preparing;
        PhaseElapsedS = 0;
        CyclesStarted++;
        if (Config.SimulatedFaultAtCycle > 0 && CyclesStarted == Config.SimulatedFaultAtCycle)
        {
            Faulted = true;
            FaultElapsedS = 0;
            Faults++;
        }
    }

    /// <summary>Avanza il ciclo. Restituisce la fase appena iniziata (o Idle a fine ciclo), null se nessun cambio.</summary>
    internal StrapperState? Advance(double dt)
    {
        if (State == StrapperState.Idle) return null;
        BusyTimeS += dt;
        if (Faulted)
        {
            FaultElapsedS += dt;
            if (FaultElapsedS >= Config.FaultResetTimeS - 1e-9)
            {
                Faulted = false;
                FaultJustCleared = true;
                PhaseElapsedS = 0;
                State = StrapperState.Preparing;
            }
            return null;
        }
        PhaseElapsedS += dt;
        StrapperState? changed = null;
        // Più fasi possono concludersi nello stesso passo se hanno durata nulla.
        for (int guard = 0; guard < 4; guard++)
        {
            double dur = State switch
            {
                StrapperState.Preparing => Config.PreparationTimeS,
                StrapperState.Strapping => Config.StrappingTimeS,
                StrapperState.Returning => Config.ReturnTimeS,
                _ => 0
            };
            if (State == StrapperState.Idle || PhaseElapsedS < dur - 1e-9) break;
            PhaseElapsedS -= dur;
            State = State switch
            {
                StrapperState.Preparing => StrapperState.Strapping,
                StrapperState.Strapping => StrapperState.Returning,
                _ => StrapperState.Idle
            };
            changed = State;
            if (State == StrapperState.Idle)
            {
                CompletedCycles++;
                PhaseElapsedS = 0;
            }
        }
        return changed;
    }

    internal void ClearPackage()
    {
        Package = null;
        StrapIndex = -1;
    }
}

public enum StopperState
{
    Free,
    Aligning,
    Aligned
}

public sealed class StopperRuntime
{
    public StopperRuntime(StopperConfig config)
    {
        Config = config;
        PositionM = config.PositionMm / 1000.0;
    }

    public StopperConfig Config { get; }
    public double PositionM { get; }
    public bool Raised => Config.IsRaised;
    public StopperState State { get; internal set; } = StopperState.Free;
    public PackageRuntime? Package { get; internal set; }
    public double ElapsedS { get; internal set; }
    public double AlignmentTotalS => Config.AlignmentTimeS + Config.HeadingTimeS;
    public double OccupiedTimeS { get; internal set; }
    public int Packages { get; internal set; }
}

public enum TransferState
{
    Home,
    Lifting,
    Translating,
    Lowering,
    Depositing,
    Returning,
    /// <summary>Traslato, in attesa che i bilici di destinazione siano liberi (interblocco).</summary>
    WaitingDestination
}

public sealed class TransferRuntime
{
    public TransferRuntime(TransferConfig config)
    {
        Config = config;
        StartM = config.PositionMm / 1000.0;
        EndM = config.EndMm / 1000.0;
    }

    public TransferConfig Config { get; }
    public double StartM { get; }
    public double EndM { get; }
    public TransferState State { get; internal set; } = TransferState.Home;
    public double ElapsedS { get; internal set; }
    /// <summary>Pacchi a bordo (più pacchi allineati possono essere prelevati nello stesso ciclo).</summary>
    public List<PackageRuntime> Packages { get; } = new();
    public PackageRuntime? Package => Packages.Count > 0 ? Packages[0] : null;
    /// <summary>Posizione verticale delle forche (0 = sotto il piano rulli).</summary>
    public double LiftM { get; internal set; }
    /// <summary>Posizione laterale del carro (0 = in linea).</summary>
    public double LateralM { get; internal set; }
    public double BusyTimeS { get; internal set; }
    public int Cycles { get; internal set; }

    public double PhaseDuration(TransferState s) => s switch
    {
        TransferState.Lifting => Config.LiftTimeS,
        TransferState.Translating => Config.EffectiveTranslationTimeS,
        TransferState.Lowering => Config.LowerTimeS,
        TransferState.Depositing => Config.DepositTimeS,
        TransferState.Returning => Config.EffectiveTranslationTimeS,
        _ => 0
    };
}
