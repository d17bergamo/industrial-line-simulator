using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Infrastructure;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Obiettivo di produttività (t/h): analisi, proposte simulate e motori commutabili sullo svincolo.</summary>
public class ProductivityAdvisorTests
{
    private static string Diag(SimulationEngine e) =>
        $"{e.FinishReason} t={e.Time:0.0}\n" + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(20).Select(x => $"{x.TimeS:0.0} {x.Message}"));

    [Fact]
    public void Target_IsASimulationSetting_InTonnesPerHour()
    {
        Assert.Equal(0, new SimulationSettings().TargetTonnesPerHour);
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Simulation.TargetTonnesPerHour = 55;
        var q = ProjectSerializer.Clone(p);
        Assert.Equal(55, q.Simulation.TargetTonnesPerHour);
    }

    [Fact]
    public void HighTarget_ProposesSimulatedChanges_ThatImproveAndWorkForAllPackages()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var r = ProductivityAdvisor.Analyze(p, targetTph: 1000, maxSteps: 2);
        Assert.True(r.BaselineTonnesPerHour > 0);
        Assert.NotEmpty(r.Options);
        Assert.Contains(r.Options, o => o.Id == "ADD_STRAPPER");
        Assert.Contains(r.Options, o => o.Id.StartsWith("SWITCHABLE_", StringComparison.Ordinal));
        Assert.NotEmpty(r.Path);
        Assert.True(r.BestTonnesPerHour > r.BaselineTonnesPerHour);
        Assert.False(r.TargetReached);
        Assert.Contains("Obiettivo non raggiunto", r.Summary, StringComparison.Ordinal);
        // il progetto proposto funziona per tutti i pacchi
        var e = new SimulationEngine(ProjectSerializer.Clone(r.BestProject!));
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.Equal(0, e.Collisions);
        // il progetto di partenza non è stato toccato
        Assert.Equal(2, p.Strappers.Count);
    }

    [Fact]
    public void TargetAlreadyReached_NoChangesProposed()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        var r = ProductivityAdvisor.Analyze(p, targetTph: 1, maxSteps: 1);
        Assert.True(r.TargetReached);
        Assert.Empty(r.Path);
        Assert.Null(r.BestProject);
        Assert.Contains("già raggiunto", r.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2 legatrici - pacchi 6/9/12 m")]
    [InlineData("3 legatrici - pacchi 6/9/12/18/24 m")]
    public void SwitchableMotors_OnTheReleaseModule_WorkCleanly(string name)
    {
        var p = SampleProjects.All.Single(m => m.Name == name).Factory();
        var m2 = p.Sections.Single(x => x.Id == "M2");
        m2.FollowsStrapping = true;
        var changes = StrappingAreaRules.DecideReleaseInverters(p);
        var topo = new LineTopology(p);
        Assert.NotSame(topo.Zone("M1"), topo.Zone("M2"));      // inverter proprio
        Assert.True(m2.FollowsStrapping);
        var e = new SimulationEngine(p);
        bool switched = false;
        while (e.Step())
            if (e.Zone("M2")!.Reason.StartsWith("Motori commutati", StringComparison.Ordinal))
            {
                switched = true;
                Assert.Equal(e.Zone("M1")!.ActualSpeed, e.Zone("M2")!.ActualSpeed, 9);
            }
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.True(switched, "i motori dello svincolo non sono mai stati commutati");
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.Equal(0, e.Collisions);
        var bad = e.Events.Where(x => x.Severity >= EventSeverity.Warning).Select(x => $"{x.TimeS:0.0} {x.Message}").Take(10).ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.Contains(ControlLogicGenerator.Generate(p).Rules, r => r.Id == "M2.COMMUTA_MOTORI");
    }
}
