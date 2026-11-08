using LineSimulator.Models;

namespace LineSimulator.Engine.Runtime;

public enum PackageState
{
    /// <summary>In movimento sui rulli.</summary>
    Transport,
    /// <summary>Fermo sui rulli in attesa (accumulo/interblocco).</summary>
    Accumulated,
    /// <summary>In posizionamento verso una fermata di legatura.</summary>
    Positioning,
    /// <summary>Fermo sotto una o più legatrici in lavoro.</summary>
    Strapping,
    /// <summary>In battuta allo stopper, allineamento/intestatura in corso.</summary>
    Aligning,
    /// <summary>Allineato, in attesa del trasferitore.</summary>
    Aligned,
    /// <summary>Sollevato dal trasferitore (fuori dai rulli).</summary>
    OnTransfer,
    /// <summary>Depositato nella stazione successiva (o scaricato dal bilico).</summary>
    Exited,
    /// <summary>In espulsione dalla rigettatrice (fuori dai rulli).</summary>
    Rejecting,
    /// <summary>Rigettato: fuori linea.</summary>
    Rejected,
    /// <summary>Sul bilico di pesatura.</summary>
    OnScale,
    /// <summary>Sul trasferitore d'accumulo (solo nelle istantanee: per il motore il pacco è già uscito).</summary>
    OnAccumulation
}

public enum StrapStatus
{
    NotRequired,
    Pending,
    InProgress,
    Done,
    Failed
}

/// <summary>Stato dinamico di un pacco. Asse x lungo la linea; testa = estremità a valle.</summary>
public sealed class PackageRuntime
{
    public PackageRuntime(int sequence, PackageType type)
    {
        Sequence = sequence;
        Id = $"P{sequence:000}";
        Type = type;
        LengthM = type.LengthMm / 1000.0;
        WeightKg = type.WeightKg;
    }

    public int Sequence { get; }
    public string Id { get; }
    public PackageType Type { get; }
    public double LengthM { get; }
    /// <summary>Peso configurato (tipologia o peso specifico del pacco).</summary>
    public double WeightKg { get; internal set; }

    public double HeadM { get; internal set; }
    public double TailM => HeadM - LengthM;
    public double Velocity { get; internal set; }
    public PackageState State { get; internal set; } = PackageState.Transport;

    public double EntryTimeS { get; internal set; }
    public double? ExitTimeS { get; internal set; }

    public StrapStatus StrapStatus { get; internal set; }
    public int StrapsDone { get; internal set; }
    public int StrapsTotal { get; internal set; }

    /// <summary>Offset verticale (sollevamento trasferitore), m.</summary>
    public double LiftM { get; internal set; }
    /// <summary>Offset laterale (traslazione trasferitore), m.</summary>
    public double LateralM { get; internal set; }

    // Identificazione (fotocellula di ingresso)
    public bool Identified { get; internal set; }
    public double? IdentifiedTimeS { get; internal set; }
    public double? MeasuredLengthM { get; internal set; }
    internal double EntryTrackStartHeadM;
    /// <summary>Riferito all'encoder di legatura (testa rilevata dalla fotocellula di riferimento).</summary>
    public bool EncoderReferenced { get; internal set; }

    // Rigetto
    public bool RejectRequested { get; internal set; }
    public bool RejectHandled { get; internal set; }
    public double? RejectedTimeS { get; internal set; }

    // Pesatura
    public double? MeasuredWeightKg { get; internal set; }
    public double? WeighedTimeS { get; internal set; }
    public bool WeighingValid { get; internal set; } = true;
    public string? ScaleId { get; internal set; }

    // Fermi (accumulo multi-pacco): -1 nessuno, 0 fermo finale, k = fermo scomparente k-1
    public int AssignedStop { get; internal set; } = -1;
    internal bool InIntermediateStopContact;
    internal bool HeldByTailStop;
    /// <summary>Ingresso "un pacco alla volta": il pacco creato attende a monte della linea finché non riceve il consenso ad avanzare.</summary>
    internal bool InfeedReleased = true;
    /// <summary>Istante in cui la coda ha liberato la fotocellula di ingresso della via rulli di legatura.</summary>
    internal double? EntryClearedTimeS;
    /// <summary>Il pacco è stato creato e attende a monte della via rulli di legatura.</summary>
    public bool WaitingAtInfeed => !InfeedReleased;

    public bool InStopperContact { get; internal set; }
    public bool IsSlipping { get; internal set; }

    // Statistiche
    public double WaitingTimeS { get; internal set; }
    public double StrappingStationTimeS { get; internal set; }
    public int AccumulationStops { get; internal set; }

    // Stati per il rilevamento degli eventi (fronte di salita)
    internal string? ContactWith;
    internal string? SafetyViolationWith;
    internal bool TransferInterference;

    public bool OnRollers => State is not (PackageState.OnTransfer or PackageState.Exited or PackageState.Rejecting
        or PackageState.Rejected or PackageState.OnScale or PackageState.OnAccumulation);
    public bool IsStopped => Math.Abs(Velocity) < 1e-4;
    public double? LeadTimeS => ExitTimeS.HasValue ? ExitTimeS.Value - EntryTimeS : null;

    public override string ToString() => $"{Id} H={HeadM:0.000} v={Velocity:0.000} {State}";
}
