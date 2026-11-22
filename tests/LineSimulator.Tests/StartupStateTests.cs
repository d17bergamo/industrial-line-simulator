using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Stato iniziale della linea (revisore B14, regola dell'utente del 2026-11-22): tutte le vie sono già vuote e ferme.</summary>
public class StartupStateTests
{
    [Theory]
    [InlineData("3 legatrici")]
    [InlineData("2 legatrici - pacchi 6/9/12/18")]
    public void EmptyLine_NoZoneRunsBeforeTheFirstPackage(string model)
    {
        var p = SampleProjects.All.Single(m => m.Name == model || m.Name.StartsWith(model, StringComparison.Ordinal)).Factory();
        p.Infeed.FirstEntryTimeS = 5;
        var e = new SimulationEngine(p);
        while (e.Time < 3 && e.Step())
            foreach (var z in e.Zones.Where(x => x.Config.AutoStopWhenEmpty))
                Assert.True(Math.Abs(z.ActualSpeed) < 1e-6, $"t={e.Time:0.0}: la via {z.Id} gira a vuoto ({z.ActualSpeed:0.00} m/s)");
    }

    [Fact]
    public void NoAutoStopEvent_AtTheStart_AndTheLineStillCompletes()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        p.Infeed.PackageCount = 4;
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.SlipEvents);
        Assert.DoesNotContain(e.Events.Where(x => x.TimeS < 1.0), x => x.Message.Contains("corsa a vuoto", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstPackage_StartsTheZonesOneAfterTheOther_WithoutSlip()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        p.Infeed.PackageCount = 1;
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Equal(0, e.SlipEvents);
        Assert.Equal(0, e.Collisions);
        Assert.Contains(e.Events, x => x.Type == SimEventType.AutoStart);
    }
}
