namespace LineSimulator.Engine;

/// <summary>
/// Simulazione "fino al regime" (regola dell'utente del 2026-11-22): il numero di pacchi lo decide il simulatore. Si parte da
/// <see cref="PackageCounts"/>[0] pacchi e si raddoppia finché la linea va a regime; la produttività si calcola solo sul periodo
/// a regime (<see cref="ResultsCalculator.SteadyWindow"/>: media mobile dei tempi ciclo entro il 3% del riferimento).
/// </summary>
public static class SteadyStateRun
{
    /// <summary>Pacchi simulati a ogni tentativo, in ordine.</summary>
    public static readonly IReadOnlyList<int> PackageCounts = new[] { 20, 40, 80, 160 };

    /// <summary>Tempi ciclo a regime che servono per dire che la linea è a regime.</summary>
    public const int MinSteadyCycles = 10;

    /// <summary>True se la simulazione è finita senza blocchi e ha abbastanza cicli a regime.</summary>
    public static bool Reached(SimulationResults r) =>
        r.Completed && !r.Deadlock && r.SteadyStateFromS.HasValue && r.SteadyStateCycles >= MinSteadyCycles;

    /// <summary>Numero d'ordine (1 = primo uscito) del pacco da cui la linea è a regime; null se non a regime.</summary>
    public static int? SteadyFromPackage(SimulationResults r)
    {
        if (r.SteadyStateFromS is not double t) return null;
        int k = r.Packages.Count(p => p.ExitTimeS.HasValue && p.ExitTimeS.Value <= t + 1e-9);
        return Math.Max(1, k);
    }
}
