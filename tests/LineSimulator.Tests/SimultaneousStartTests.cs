using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// Decisione dell'utente (2026-11-13 14:01): il pacco nuovo parte insieme a quello che sta finendo di legare, senza inverter
/// in comune; se si ferma quello a monte, quello a valle prosegue da solo. Svincolo con inverter proprio e parcheggio valutati
/// dall'ottimizzatore sulla produttività.
/// </summary>
public class SimultaneousStartTests
{
    /// <summary>Svincolo allungato finché ci sta il pacco più corto con i margini, inverter proprio (motori commutabili se la legatura lo impegna).</summary>
    public static bool ApplyParking(LineProject p)
    {
        var m = StrappingAreaRules.ReleaseModules(p).LastOrDefault();
        if (m == null) return false;
        double need = StrappingAreaRules.ShortestPackageMm(p) + StrappingAreaRules.ParkingMarginMm;
        int rollers = Math.Max(0, (int)Math.Ceiling((need - m.LengthMm) / m.RollerPitchMm - 1e-9));
        double oldEnd = m.EndMm;
        LineReflow.Fit(m, m.StartMm, oldEnd + rollers * m.RollerPitchMm);
        LineReflow.AfterResize(p, m, oldEnd);
        ProjectNormalizer.AlignStopperToLastRoller(p);
        m.CommandZoneId = string.Empty;
        if (StrappingAreaRules.RequiredStrappingHeadMaxMm(p) > (StrappingAreaRules.StrappingModule(p)?.EndMm ?? double.MaxValue) + 1e-6)
            m.FollowsStrapping = true;
        return true;
    }

    private static SimulationEngine Run(LineProject p)
    {
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Equal(0, e.Collisions);
        var bad = e.Events.Where(x => x.Severity >= EventSeverity.Warning).Select(x => x.ToString()).Take(10).ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        return e;
    }

    [Fact]
    public void WithOwnInverterOnTheReleaseModule_TheNewPackageStartsTogether_AndNeverStopsTheStrappedOne()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var m = StrappingAreaRules.ReleaseModules(p).Last();
        m.CommandZoneId = string.Empty;
        m.FollowsStrapping = true;
        var e = Run(p);
        var starts = e.Events.Where(x => x.Message.Contains("parte insieme")).ToList();
        Assert.NotEmpty(starts);
        // dopo la partenza insieme il legato lascia la via di legatura senza fermarsi finché ha la coda su di essa
        double strapEnd = StrappingAreaRules.StrappingModule(e.Project)!.EndMm / 1000.0;
        foreach (var ev in starts)
        {
            var next = e.Packages.Single(k => k.Id == ev.PackageId);
            string prevId = $"P{next.Sequence - 1:000}";
            var frames = e.Recorder.Frames.Where(f => f.TimeS > ev.TimeS + 2).ToList();
            foreach (var f in frames)
            {
                var prev = f.Packages.FirstOrDefault(k => k.Id == prevId);
                if (prev == null || prev.HeadM - prev.LengthM >= strapEnd) break;
                Assert.True(Math.Abs(prev.Velocity) > 1e-3, $"{prevId} fermo a {f.TimeS:0.0} s con la coda sulla via di legatura dopo la partenza di {next.Id}");
            }
        }
    }

    [Fact]
    public void Parking_IsProposedByTheOptimizer_AndRunsClean()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var r = ProductivityAdvisor.Analyze(p, 200, maxSteps: 1);
        var park = r.Options.SingleOrDefault(o => o.Id.StartsWith("PARK_"));
        Assert.NotNull(park);
        Assert.True(park!.Feasible, park.Problem);
        var q = SampleProjects.CreateTwoStrappers12m();
        Assert.True(ApplyParking(q));
        Assert.True(StrappingAreaRules.CanPark(q, StrappingAreaRules.ReleaseModules(q).Last()));
        Run(q);
    }
}
