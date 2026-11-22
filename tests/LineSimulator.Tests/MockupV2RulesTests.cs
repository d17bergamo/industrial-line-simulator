using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>Regole del mock-up web v2 (2026-11-22): svincolo unico non rimovibile, simulazione fino al regime.</summary>
public class MockupV2RulesTests
{
    [Fact]
    public void OnlyReleaseModule_CannotBeDeleted()
    {
        var p = SampleProjects.All[0].Factory();
        var rel = StrappingAreaRules.ReleaseModules(p);
        if (rel.Count == 0) return;   // modello senza svincolo: niente da verificare
        for (int guard = 0; guard < 10 && StrappingAreaRules.ReleaseModules(p).Count > 1; guard++)
        {
            ProjectEditing.Delete(p, new object[] { StrappingAreaRules.ReleaseModules(p)[0] });
            ProjectEditing.Normalize(p);
        }
        var last = StrappingAreaRules.ReleaseModules(p)[0];
        Assert.True(ProjectEditing.IsProtectedSection(p, last));
        Assert.False(ProjectEditing.CanDelete(p, last));
    }

    [Fact]
    public void SteadyState_NeedsCompletedRunAndEnoughSteadyCycles()
    {
        static PackageResult Pkg(int i, double exit) =>
            new($"P{i}", "T1", 6000, 3000, 0, exit, exit, 0, 0, StrapStatus.Done, 4, 4);
        var pkgs = Enumerable.Range(1, 20).Select(i => Pkg(i, 30.0 * i)).ToList();

        var ok = new SimulationResults { Completed = true, SteadyStateFromS = 90, SteadyStateCycles = SteadyStateRun.MinSteadyCycles, Packages = pkgs };
        Assert.True(SteadyStateRun.Reached(ok));
        Assert.Equal(3, SteadyStateRun.SteadyFromPackage(ok));

        var few = new SimulationResults { Completed = true, SteadyStateFromS = 90, SteadyStateCycles = SteadyStateRun.MinSteadyCycles - 1, Packages = pkgs };
        Assert.False(SteadyStateRun.Reached(few));

        var stopped = new SimulationResults { Completed = false, SteadyStateFromS = 90, SteadyStateCycles = 50, Packages = pkgs };
        Assert.False(SteadyStateRun.Reached(stopped));
    }
}
