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
        // svincolo con inverter dedicato e motori commutabili è già il default: non è più una proposta
        Assert.DoesNotContain(r.Options, o => o.Id.StartsWith("SWITCHABLE_", StringComparison.Ordinal));
        Assert.Null(r.Saving);                                 // obiettivo non raggiunto: niente proposta di risparmio
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

    [Fact]
    public void ReleaseModule_DedicatedByDefault_JoinProposedOnlyAbove25PercentOverTarget()
    {
        // regola dell'utente (2026-11-13 23:20): di default svincolo con inverter dedicato
        var p = SampleProjects.CreateTwoStrappers12m();
        var topo = new LineTopology(p);
        Assert.NotSame(topo.Zone("M1"), topo.Zone("M2"));
        Assert.True(p.Sections.Single(x => x.Id == "M2").FollowsStrapping);
        var baseline = ProductivityAdvisor.Analyze(p, 0, maxSteps: 0).BaselineTonnesPerHour;
        Assert.True(baseline > 0);

        // obiettivo superato di oltre il 25% → proposta di unire gli inverter, con l'obiettivo ancora raggiunto
        double low = baseline / 1.4;
        var r = ProductivityAdvisor.Analyze(p, low, maxSteps: 1);
        Assert.True(r.TargetReached);
        Assert.NotNull(r.Saving);
        Assert.NotNull(r.SavingProject);
        Assert.True(r.Saving!.TonnesPerHour >= low);
        Assert.Contains("Proposta di risparmio", r.Summary, StringComparison.Ordinal);
        var q = r.SavingProject!;
        Assert.True(q.Sections.Single(x => x.Id == "M2").JoinStrappingInverter);
        var tq = new LineTopology(q);
        Assert.Same(tq.Zone("M1"), tq.Zone("M2"));
        var e = new SimulationEngine(ProjectSerializer.Clone(q));
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(0, e.Collisions);

        // margine sotto il 25% → nessuna proposta
        var r2 = ProductivityAdvisor.Analyze(p, baseline / 1.1, maxSteps: 1);
        Assert.True(r2.TargetReached);
        Assert.Null(r2.Saving);
    }

    [Fact]
    public void Strappers_AreOnlyAdded_SameSide_AtTheExistingPitch()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Strappers[1].PositionMm = p.Strappers[0].PositionMm + 3000;      // passo attuale 3000 mm
        var before = p.Strappers.Select(s => (s.Id, s.PositionMm, s.Side)).ToList();
        var r = ProductivityAdvisor.Analyze(p, targetTph: 1000, maxSteps: 3);   // con la via di legatura a regola il primo passo può essere un altro (motori commutabili)
        Assert.DoesNotContain(r.Options, o => o.Id is "SPACING_2M" or "ALTERNATE_SIDES");
        Assert.Contains(r.Options, o => o.Id == "ADD_STRAPPER");
        Assert.Contains(r.Path, o => o.Id == "ADD_STRAPPER");
        var q = r.BestProject!;
        Assert.Equal(3, q.Strappers.Count);
        foreach (var (id, pos, side) in before)
        {
            var s = q.Strappers.Single(x => x.Id == id);
            Assert.Equal(pos, s.PositionMm, 6);                              // le esistenti non si spostano
            Assert.Equal(side, s.Side);
        }
        var added = q.Strappers.Single(x => before.All(b => b.Id != x.Id));
        Assert.Equal(before[1].PositionMm + 3000, added.PositionMm, 6);      // stesso passo delle due esistenti
        Assert.Equal(before[1].Side, added.Side);                            // stesso lato
    }
}
