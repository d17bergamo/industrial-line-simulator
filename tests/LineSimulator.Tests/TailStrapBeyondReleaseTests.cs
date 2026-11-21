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

    [Theory]
    [InlineData(18)]
    [InlineData(24)]
    public void ShortRelease_TransferTrackFollowsTheStrapping_OnlyWhenTheTailStrapNeedsIt(int lengthM)
    {
        var p = ShortReleaseProject(lengthM, 2);
        var e = new SimulationEngine(p);
        var trf = LayoutRules.TransferModule(p)!;
        Assert.True(trf.FollowsStrapping, "la via del trasferitore deve essere commutabile sulla legatura");
        Assert.Contains(e.Preparation.Changes, c => c.Contains("via del trasferitore", StringComparison.OrdinalIgnoreCase) || c.Contains(trf.Id));
        // senza i pacchi lunghi la legatura di coda non esce dallo svincolo: niente commutazione della via del trasferitore
        var q = ShortReleaseProject(9, 2);
        q.PackageTypes.RemoveAll(t => t.LengthMm > 12000);
        _ = new SimulationEngine(q);
        Assert.False(LayoutRules.TransferModule(q)!.FollowsStrapping);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(24)]
    public void ShortRelease_BeyondTheExit_StrappingStartsOnlyWithTheTransferAtHome(int lengthM)
    {
        var p = ShortReleaseProject(lengthM);
        var e = new SimulationEngine(p);
        double entry = StrappingAreaRules.AccumulationEntryMm(p) / 1000.0;
        int checkedScans = 0;
        while (e.Step())
        {
            foreach (var k in e.Packages)
            {
                if (k.State is not (PackageState.Positioning or PackageState.Strapping) || k.HeadM <= entry + 1e-3) continue;
                checkedScans++;
                Assert.True(e.Transfer.State == TransferState.Home, $"t={e.Time:0.0}: {k.Id} in legatura oltre l'uscita dello svincolo con il trasferitore {e.Transfer.State}");
            }
        }
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.True(checkedScans > 0, "nessun pacco ha superato l'uscita dello svincolo durante la legatura");
    }
}

/// <summary>
/// Svincolo lungo diviso in sezioni-parcheggio (regola dell'utente, 2026-11-20): il massimo numero di sezioni sul pacco più
/// lungo, rulli interi il più possibile uguali, ognuna con il suo inverter e la sua fotocellula.
/// </summary>
public class LongReleaseSectionsTests
{
    private static string Diag(SimulationEngine e) =>
        $"{e.FinishReason} t={e.Time:0.0} collisioni={e.Collisions} strisciamenti={e.SlipEvents}\n"
        + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(20).Select(x => $"[{x.TimeS:0.0}] {x.Message}"));

    /// <summary>3 legatrici, svincolo di 100 m (80 rulli), pacchi fino a 12 m, una sola misura di pacco.</summary>
    private static LineProject LongReleaseProject(int packages = 6)
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        p.PackageTypes.RemoveAll(t => t.LengthMm > 12000);
        var rel = StrappingAreaRules.ReleaseModules(p).Single();
        double oldEnd = rel.EndMm;
        LineReflow.Fit(rel, rel.StartMm, rel.StartMm + 80 * rel.RollerPitchMm);
        LineReflow.AfterResize(p, rel, oldEnd);
        p.Infeed.TypeSequence = new List<string> { p.PackageTypes.Single(t => Math.Abs(t.LengthMm - 12000) < 1).Id };
        p.Infeed.PackageCount = packages;
        return p;
    }

    [Fact]
    public void Release100m_IsSplitInSixSectionsOfWholeRollers_EachWithItsOwnInverterAndPhotocell()
    {
        var p = LongReleaseProject();
        var e = new SimulationEngine(p);
        Assert.Contains(e.Preparation.Changes, c => c.Contains("diviso in 6 sezioni", StringComparison.Ordinal));
        var sections = StrappingAreaRules.ReleaseModules(p);
        Assert.Equal(6, sections.Count);
        Assert.Equal(new[] { 14, 14, 13, 13, 13, 13 }, sections.Select(s => s.RollerCount).ToArray());
        Assert.Equal(80, sections.Sum(s => s.RollerCount));
        for (int i = 1; i < sections.Count; i++) Assert.Equal(sections[i - 1].EndMm, sections[i].StartMm, 6);
        Assert.All(sections, s => Assert.Equal(s.Id, s.EffectiveZoneId));                 // un inverter per sezione
        Assert.Equal(sections.Count, sections.Select(s => s.Id).Distinct().Count());
        // ogni sezione ha una fotocellula che la comanda: le prime cinque di accumulo, l'ultima quella di arresto davanti al trasferitore
        foreach (var s in sections)
            Assert.Contains(p.Photocells, pc => pc.ControlledInverterId == s.Id);
        Assert.Equal(sections[^1].Id, p.Photocells.Single(pc => pc.ActsAsTransferStop).ControlledInverterId);
    }

    [Fact]
    public void Release100m_Simulates_WithoutCollisionsOrSlip()
    {
        var p = LongReleaseProject();
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.SlipEvents);
        Assert.Equal(p.Infeed.PackageCount, e.Packages.Count(k => k.State == PackageState.Exited));
    }

    [Fact]
    public void ShortRelease_IsNotSplit()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var e = new SimulationEngine(p);
        Assert.DoesNotContain(e.Preparation.Changes, c => c.Contains("diviso in", StringComparison.Ordinal));
        Assert.Single(StrappingAreaRules.ReleaseModules(p));
    }

    /// <summary>3 legatrici, svincolo di 75 m (60 rulli), tutti i pacchi fino a 24 m (B12).</summary>
    private static LineProject Release75mProject(int lengthMm, int packages = 4)
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var rel = StrappingAreaRules.ReleaseModules(p).Single();
        double oldEnd = rel.EndMm;
        LineReflow.Fit(rel, rel.StartMm, rel.StartMm + 60 * rel.RollerPitchMm);
        LineReflow.AfterResize(p, rel, oldEnd);
        p.Infeed.TypeSequence = new List<string> { p.PackageTypes.Single(t => Math.Abs(t.LengthMm - lengthMm) < 1).Id };
        p.Infeed.PackageCount = packages;
        return p;
    }

    [Fact]
    public void Release75m_SectionsAreSizedOn12m_EvenWhenTheLongestIs24m()
    {
        var p = Release75mProject(24000);
        var e = new SimulationEngine(p);
        var sections = StrappingAreaRules.ReleaseModules(p);
        Assert.Equal(5, sections.Count);
        Assert.All(sections, s => Assert.Equal(12, s.RollerCount));                       // mai sotto i 12 m di riferimento
        Assert.All(sections.Take(4), s => Assert.Contains(p.Photocells, pc => pc.ControlledInverterId == s.Id && pc.Action == PhotocellAction.Accumulation));
        Assert.Contains(e.Preparation.Changes, c => c.Contains("diviso in 5 sezioni", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(6000)]
    [InlineData(24000)]
    public void Release75m_LongOrShortPackages_SimulateWithoutCollisionsOrSlip(int lengthMm)
    {
        var p = Release75mProject(lengthMm);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.SlipEvents);
        Assert.Equal(p.Infeed.PackageCount, e.Packages.Count(k => k.State == PackageState.Exited));
    }

    [Fact]
    public void Release_ShorterThanTheLongestPackagePlusMargin_StaysOneSection()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var rel = StrappingAreaRules.ReleaseModules(p).Single();
        double oldEnd = rel.EndMm;
        double longest = p.PackageTypes.Max(t => t.LengthMm);
        int rollers = (int)Math.Floor((longest + StrappingAreaRules.ParkingMarginMm) / rel.RollerPitchMm) - 1;
        LineReflow.Fit(rel, rel.StartMm, rel.StartMm + rollers * rel.RollerPitchMm);
        LineReflow.AfterResize(p, rel, oldEnd);
        var e = new SimulationEngine(p);
        Assert.DoesNotContain(e.Preparation.Changes, c => c.Contains("diviso in", StringComparison.Ordinal));
    }
}
