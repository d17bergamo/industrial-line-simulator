using LineSimulator.Engine;
using LineSimulator.Models;

namespace LineSimulator.Infrastructure;

/// <summary>Tempo medio per pacco e produttività di una tipologia sulla linea (tabella della schermata Pacchi).</summary>
public sealed record PackageProductivityResult(string TypeId, double? CycleTimeS, double? TonnesPerHour, double? PackagesPerHour, string? Problem);

public static class PackageProductivity
{
    /// <summary>Copia del progetto pronta per simulare solo la tipologia <paramref name="typeId"/> (12 pacchi, non continua).</summary>
    public static LineProject PrepareFor(LineProject source, string typeId, int packages = 12)
    {
        var p = ProjectSerializer.Clone(source);
        try { ProjectAutoFix.Run(p); } catch { /* progetto incompleto: si simula com'è */ }
        p.Infeed.TypeSequence = new List<string> { typeId };
        p.Infeed.Continuous = false;
        p.Infeed.PackageCount = packages;
        return p;
    }

    /// <summary>Simula solo quella tipologia e restituisce il tempo ciclo a regime e la produttività.</summary>
    public static PackageProductivityResult Evaluate(LineProject source, string typeId, double? lineSpeedMps = null, CancellationToken ct = default)
    {
        try
        {
            var p = PrepareFor(source, typeId);
            var e = new SimulationEngine(p);
            if (lineSpeedMps is double v) e.SetLineSpeed(v);
            while (e.Step()) ct.ThrowIfCancellationRequested();
            var r = e.ComputeResults();
            string? problem = r.Collisions > 0 ? "collisioni" : r.Deadlock ? "blocco" : r.PackagesExited == 0 ? "nessun pacco uscito" : null;
            return new PackageProductivityResult(typeId, r.CycleTimeMeanS, r.TonnesPerHour, r.PackagesPerHour, problem);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new PackageProductivityResult(typeId, null, null, null, ex.Message); }
    }
}
