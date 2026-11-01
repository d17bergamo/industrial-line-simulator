using System.Globalization;

namespace LineSimulator.Optimization;

/// <summary>Dati di una legatrice per l'ottimizzatore (unità SI).</summary>
public sealed record StrapperSpec(
    string Id,
    string Name,
    int Index,
    double PositionM,
    double PreparationS,
    double StrappingS,
    double ReturnS,
    bool AllowCombined)
{
    public double CycleS => PreparationS + StrappingS + ReturnS;
}

/// <summary>
/// Problema di legatura per un pacco: il pacco avanza solo in avanti e si ferma in posizioni
/// (posizione della testa H) in cui una o più legatrici si trovano sopra una legatura:
/// X_legatrice = H − d_legatura. Le legature di una stessa fermata sono eseguite in parallelo.
/// </summary>
public sealed class StrappingProblem
{
    public required IReadOnlyList<StrapperSpec> Strappers { get; init; }
    /// <summary>Posizioni delle legature dalla testa del pacco (m), nell'ordine del pattern.</summary>
    public required IReadOnlyList<double> StrapPositionsM { get; init; }
    public double PackageLengthM { get; init; }
    /// <summary>Posizioni ammesse della testa durante la legatura (pacco interamente nella zona).</summary>
    public double HeadMinM { get; init; }
    public double HeadMaxM { get; init; }
    /// <summary>Posizione testa all'ingresso in stazione (inizio conteggio tempo).</summary>
    public double ReferenceHeadM { get; init; }
    /// <summary>Posizione testa a fine conteggio (coda fuori dalla zona).</summary>
    public double ExitHeadM { get; init; }
    public double TransportSpeed { get; init; }
    public double PositioningSpeed { get; init; }
    public double Acceleration { get; init; }
    public double Deceleration { get; init; }
    public double MatchToleranceM { get; init; } = 0.0005;
}

/// <summary>Singola legatura assegnata a una legatrice.</summary>
public sealed record StrapTask(string StrapperId, int StrapIndex, double CycleS);

public sealed class PlannedStop
{
    public double HeadPositionM { get; init; }
    public List<StrapTask> Tasks { get; init; } = new();
    /// <summary>Tempo di spostamento per raggiungere questa fermata.</summary>
    public double MoveTimeS { get; init; }
    /// <summary>Durata della lavorazione alla fermata (max dei cicli in parallelo).</summary>
    public double WorkTimeS => Tasks.Count == 0 ? 0 : Tasks.Max(t => t.CycleS);
}

/// <summary>Piano di legatura di un pacco con tempo stimato della stazione.</summary>
public sealed class StrappingPlan
{
    public string StrategyName { get; init; } = string.Empty;
    public bool IsFeasible { get; init; }
    public string? InfeasibleReason { get; init; }
    public List<PlannedStop> Stops { get; init; } = new();
    public double ExitMoveTimeS { get; init; }
    public IReadOnlyList<string> CandidateStrapperIds { get; init; } = Array.Empty<string>();

    public double MoveTimeS => Stops.Sum(s => s.MoveTimeS) + ExitMoveTimeS;
    public double WorkTimeS => Stops.Sum(s => s.WorkTimeS);
    public double TotalTimeS => IsFeasible ? MoveTimeS + WorkTimeS : double.PositiveInfinity;
    public int ParallelStops => Stops.Count(s => s.Tasks.Count > 1);
    public double TotalStrapperBusyS => Stops.Sum(s => s.Tasks.Sum(t => t.CycleS));
    public IReadOnlyList<string> UsedStrapperIds =>
        Stops.SelectMany(s => s.Tasks).Select(t => t.StrapperId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    public string Summary
    {
        get
        {
            var ci = CultureInfo.InvariantCulture;
            if (!IsFeasible) return $"{StrategyName}: NON AMMISSIBILE - {InfeasibleReason}";
            string machines = string.Join("+", UsedStrapperIds);
            return $"{StrategyName}: {machines}, {Stops.Count} fermat{(Stops.Count == 1 ? "a" : "e")}, " +
                   $"{ParallelStops} in parallelo, tempo stazione {TotalTimeS.ToString("0.0", ci)} s " +
                   $"(movimenti {MoveTimeS.ToString("0.0", ci)} s, legatura {WorkTimeS.ToString("0.0", ci)} s)";
        }
    }

    public string Describe()
    {
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string> { Summary };
        int i = 1;
        foreach (var s in Stops)
        {
            string tasks = string.Join(", ", s.Tasks.Select(t => $"{t.StrapperId}→legatura #{t.StrapIndex + 1}"));
            lines.Add($"  Fermata {i++}: testa a {(s.HeadPositionM * 1000).ToString("0", ci)} mm, spostamento {s.MoveTimeS.ToString("0.0", ci)} s, lavoro {s.WorkTimeS.ToString("0.0", ci)} s [{tasks}]");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static StrappingPlan Infeasible(string strategy, string reason, IReadOnlyList<string>? ids = null) =>
        new() { StrategyName = strategy, IsFeasible = false, InfeasibleReason = reason, CandidateStrapperIds = ids ?? Array.Empty<string>() };
}
