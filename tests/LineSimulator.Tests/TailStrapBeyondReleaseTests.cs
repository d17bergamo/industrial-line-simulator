using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>
/// Legatura di coda che porta il pacco oltre lo svincolo (revisore B10, regola dell'utente del 2026-11-21): 3 legatrici,
/// svincolo corto (5 m), pacchi lunghi. Il pacco può andare oltre lo svincolo; oltre la fotocellula di uscita dello svincolo
/// la legatura parte solo con il trasferitore a riposo e la via davanti libera. Niente collisioni, strisciamenti o blocchi.
/// </summary>
public class TailStrapBeyondReleaseTests
{
    private static string Diag(SimulationEngine e) =>
        $"{e.FinishReason} t={e.Time:0.0} collisioni={e.Collisions} strisciamenti={e.SlipEvents}\n"
        + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(20).Select(x => $"[{x.TimeS:0.0}] {x.Message}"))
        + "\n--- ultimi eventi\n"
        + string.Join("\n", e.Events.TakeLast(12).Select(x => $"[{x.TimeS:0.0}] {x.Message}"));

    /// <summary>Esempio 3 legatrici 24 m con lo svincolo accorciato a 5 m (4 rulli) e una sola misura di pacco.</summary>
    private static LineProject ShortReleaseProject(int lengthM, int packages = 6)
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var rel = StrappingAreaRules.ReleaseModules(p).Single();
        double oldEnd = rel.EndMm;
        LineReflow.Fit(rel, rel.StartMm, rel.StartMm + 4 * rel.RollerPitchMm);
        LineReflow.AfterResize(p, rel, oldEnd);
        var type = p.PackageTypes.Single(t => Math.Abs(t.LengthMm - lengthM * 1000.0) < 1);
        p.Infeed.TypeSequence = new List<string> { type.Id };
        p.Infeed.PackageCount = packages;
        return p;
    }

    [Theory]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(24)]
    public void ShortRelease_LongPackages_CompleteWithoutCollisionsOrSlip(int lengthM)
    {
        var p = ShortReleaseProject(lengthM);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.False(e.Deadlocked, Diag(e));
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.SlipEvents);
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.Equal(p.Infeed.PackageCount, e.Packages.Count(k => k.State == PackageState.Exited));
    }
}
