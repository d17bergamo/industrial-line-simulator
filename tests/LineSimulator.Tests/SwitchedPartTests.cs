using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// B9 (decisione dell'utente, 2026-11-23: "tratto esatto"): dei motori dello svincolo sono commutabili solo i rulli che servono
/// alla legatura di coda; il resto è una via a sé con il suo inverter, che non segue la legatura.
/// </summary>
public class SwitchedPartTests
{
    /// <summary>3 legatrici, svincolo di 25 m (20 rulli: non si divide in sezioni-parcheggio), pacchi da 24 m.</summary>
    private static LineProject Release25m(int packages = 4)
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var rel = StrappingAreaRules.ReleaseModules(p).Single();
        double oldEnd = rel.EndMm;
        LineReflow.Fit(rel, rel.StartMm, rel.StartMm + 20 * rel.RollerPitchMm);
        LineReflow.AfterResize(p, rel, oldEnd);
        p.Infeed.TypeSequence = new List<string> { p.PackageTypes.OrderByDescending(t => t.LengthMm).First().Id };
        p.Infeed.PackageCount = packages;
        return p;
    }

    [Fact]
    public void LongRelease_OnlyNeededRollersAreSwitchable()
    {
        var p = Release25m();
        double need = StrappingAreaRules.RequiredStrappingHeadMaxMm(p);
        var e = new SimulationEngine(p);
        var rel = StrappingAreaRules.ReleaseModules(p);
        Assert.Equal(2, rel.Count);
        var sw = rel[0];
        var rest = rel[1];
        Assert.True(sw.FollowsStrapping);
        Assert.True(rest.SwitchRest);
        Assert.False(rest.FollowsStrapping);
        Assert.Equal(rest.Id, rest.EffectiveZoneId);                                     // il resto ha il suo inverter
        Assert.True(sw.EndMm >= need - 1e-6 && sw.EndMm - need < sw.RollerPitchMm, $"parte commutabile fino a {sw.EndMm:0} mm, testa a {need:0} mm");
        Assert.Contains(e.Preparation.Changes, c => c.Contains("motori commutabili solo sui", StringComparison.Ordinal));
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.SlipEvents);
    }

    [Fact]
    public void SwitchedPart_IsStableOnASecondPreparation()
    {
        var p = Release25m();
        _ = new SimulationEngine(p);
        string first = ProjectSerializer.Serialize(p);
        var r = SimulationPreparation.Prepare(p);
        Assert.Equal(first, ProjectSerializer.Serialize(p));
        Assert.DoesNotContain(r.Changes, c => c.Contains("motori commutabili solo sui", StringComparison.Ordinal) || c.Contains("riunita", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitchedPart_IsMergedBack_WhenTheLongPackagesAreRemoved()
    {
        var p = Release25m();
        _ = new SimulationEngine(p);
        p.PackageTypes.RemoveAll(t => t.LengthMm > 6000);                                // la legatura non impegna più lo svincolo
        p.Infeed.TypeSequence = new List<string> { p.PackageTypes.OrderBy(t => t.LengthMm).First().Id };
        _ = new SimulationEngine(p);
        var rel = StrappingAreaRules.ReleaseModules(p);
        Assert.DoesNotContain(rel, s => s.SwitchRest);
        Assert.Single(rel);
        Assert.Equal(20, rel[0].RollerCount);
    }

    [Fact]
    public void LongRelease_TailStrapDoesNotWaitForTheWholeRelease()
    {
        var p = Release25m(packages: 6);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        var rest = StrappingAreaRules.ReleaseModules(p).Single(s => s.SwitchRest);
        Assert.DoesNotContain(e.Events, x => x.Message.Contains($"Attesa svincolo {rest.Id}", StringComparison.Ordinal));
    }
}
