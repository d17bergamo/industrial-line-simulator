using LineSimulator.Core;
using LineSimulator.Engine;
namespace LineSimulator.Tests;
public class ZzCompareTests
{
    [Fact]
    public void Compare()
    {
        var lines = new List<string>();
        foreach (var (name, f) in SampleProjects.All)
        {
            var a = f(); var b = f();
            b.Simulation.AutoReleaseInverters = false;
            foreach (var m in StrappingAreaRules.ReleaseModules(b)) { m.FollowsStrapping = false; m.CommandZoneId = "M1"; }
            var ea = new SimulationEngine(a); ea.RunToCompletion(); var ra = ea.ComputeResults();
            var eb = new SimulationEngine(b); eb.RunToCompletion(); var rb = eb.ComputeResults();
            int waits = ea.Events.Count(x => x.Message.Contains("Attesa svincolo", StringComparison.Ordinal));
            lines.Add($"{name}: NUOVO pph={ra.PackagesPerHour:0.0} ciclo={ra.CycleTimeMeanS:0.0}s tot={ea.Time:0} warn={ea.Events.Count(x => x.Severity >= EventSeverity.Warning)} | UNICO pph={rb.PackagesPerHour:0.0} ciclo={rb.CycleTimeMeanS:0.0}s tot={eb.Time:0} warn={eb.Events.Count(x => x.Severity >= EventSeverity.Warning)}");
        }
        Assert.Fail(string.Join("\n", lines));
    }
}
