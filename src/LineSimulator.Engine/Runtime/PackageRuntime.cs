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
    /// <summary>Depositato nella stazione successiva.</summary>
    Exited
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
    public double WeightKg { get; }

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

    public bool OnRollers => State != PackageState.OnTransfer && State != PackageState.Exited;
    public bool IsStopped => Math.Abs(Velocity) < 1e-4;
    public double? LeadTimeS => ExitTimeS.HasValue ? ExitTimeS.Value - EntryTimeS : null;

    public override string ToString() => $"{Id} H={HeadM:0.000} v={Velocity:0.000} {State}";
}
