using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

public class LineIntegrationTests
{
    private static SimulationEngine RunStandard(Action<LineProject>? tweak = null)
    {
        var p = SampleProjects.CreateStandardLine();
        tweak?.Invoke(p);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        return e;
    }

    [Fact]
    public void StandardLine_IsValid()
    {
        var r = ProjectValidator.Validate(SampleProjects.CreateStandardLine());
        Assert.False(r.HasErrors, string.Join("\n", r.Errors));
    }

    [Fact]
    public void SingleStrapperLine_IsValid()
    {
        var r = ProjectValidator.Validate(SampleProjects.CreateSingleStrapperLine());
        Assert.False(r.HasErrors, string.Join("\n", r.Errors));
    }

    [Fact]
    public void StandardLine_CompletesWithoutCollisionsOrDeadlock()
    {
        var e = RunStandard();
        Assert.True(e.CompletedNormally, e.FinishReason + "\n" + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning)));
        Assert.Equal(10, e.Packages.Count(p => p.State == PackageState.Exited));
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.SafetyViolations);
        Assert.Equal(0, e.ZoneOverfills);
        Assert.Equal(0, e.PositioningErrors);
        Assert.Equal(0, e.ExcessiveImpacts);
        Assert.False(e.Deadlocked);
        Assert.All(e.Packages, p => Assert.Equal(StrapStatus.Done, p.StrapStatus));
        Assert.All(e.Packages, p => Assert.Equal(3, p.StrapsDone));
    }

    [Fact]
    public void StandardLine_AccumulatesUpstreamOfStrapping()
    {
        var e = RunStandard();
        // L'alimentazione (30 s) è più veloce della stazione di legatura: deve formarsi accumulo.
        Assert.True(e.AccumulationStops > 0);
        Assert.True(e.MaxSimultaneousAccumulated >= 1);
        Assert.Contains(e.Events, x => x.Type == SimEventType.AccumulationStop);
    }

    [Fact]
    public void StandardLine_MinimumDistanceRespectedInEveryFrame()
    {
        var e = RunStandard();
        double min = e.Project.Simulation.MinSafetyDistanceMm / 1000.0;
        foreach (var f in e.Recorder.Frames)
        {
            var onRollers = f.Packages.Where(p => p.State != PackageState.OnTransfer && p.State != PackageState.Exited)
                .OrderByDescending(p => p.HeadM).ToList();
            for (int i = 1; i < onRollers.Count; i++)
            {
                double gap = (onRollers[i - 1].HeadM - onRollers[i - 1].LengthM) - onRollers[i].HeadM;
                Assert.True(gap >= min - 1e-6, $"t={f.TimeS:0.00}: gap {gap:0.000} m tra {onRollers[i - 1].Id} e {onRollers[i].Id}");
            }
        }
    }

    [Fact]
    public void StandardLine_UpstreamPackageStopsInPrecedingZoneWhenDownstreamBlocked()
    {
        var p = SampleProjects.CreateStandardLine();
        var e = new SimulationEngine(p);
        bool seen = false;
        while (e.Step())
        {
            var inv2 = e.Zone("INV2")!;
            if (inv2.Mode == InverterMode.Stop && inv2.BlockedByAccumulation && !inv2.IsMoving)
            {
                // Il pacco fermo in INV2 deve essere davanti a FC2 (non oltre la fine zona)
                var pk = e.Packages.FirstOrDefault(x => x.OnRollers && x.HeadM > inv2.Info.StartM && x.HeadM <= inv2.Info.EndM);
                if (pk != null)
                {
                    Assert.True(pk.HeadM >= 24.5 - 1e-6 && pk.HeadM <= inv2.Info.EndM);
                    Assert.False(e.Zone("INV3")!.AcceptsFor(pk));
                    seen = true;
                    break;
                }
            }
        }
        Assert.True(seen, "Nessun arresto per accumulo osservato in INV2");
    }

    [Fact]
    public void DangerousConfiguration_CollisionIsDetected()
    {
        var p = TestLines.TwoZonesWithoutAccumulation();
        var e = new SimulationEngine(p);
        e.RunUntil(200);
        Assert.True(e.Collisions > 0);
        Assert.Contains(e.Events, x => (x.Type == SimEventType.Collision || x.Type == SimEventType.TransferInterference) && x.Severity == EventSeverity.Error);
    }

    [Fact]
    public void Simulation_IsRepeatable()
    {
        var a = RunStandard();
        var b = RunStandard();
        Assert.Equal(a.Time, b.Time);
        Assert.Equal(a.Events.Count, b.Events.Count);
        for (int i = 0; i < a.Events.Count; i++)
        {
            Assert.Equal(a.Events[i].TimeS, b.Events[i].TimeS);
            Assert.Equal(a.Events[i].Message, b.Events[i].Message);
        }
        Assert.Equal(a.Packages.Select(p => p.ExitTimeS), b.Packages.Select(p => p.ExitTimeS));
    }

    [Fact]
    public void Results_ProductivityConsistentWithCycleTime()
    {
        var e = RunStandard();
        var r = e.ComputeResults();
        Assert.NotNull(r.CycleTimeMeanS);
        Assert.Equal(3600.0 / r.CycleTimeMeanS!.Value, r.PackagesPerHour!.Value, 6);
        Assert.Equal(r.PackagesPerHour!.Value * 3000 / 1000.0, r.TonnesPerHour!.Value, 6);
        Assert.True(r.CycleTimeMinS <= r.CycleTimeMeanS && r.CycleTimeMeanS <= r.CycleTimeMaxS);
        // Teorico senza spaziatura: 3600·0.6/12 = 180 pacchi/h ; l'effettivo deve essere inferiore
        Assert.Equal(180, r.TheoreticalPackagesPerHour, 6);
        Assert.True(r.PackagesPerHour < r.TheoreticalPackagesPerHour);
        Assert.False(string.IsNullOrEmpty(r.BottleneckName));
        Assert.Contains(r.BottleneckId, new[] { "INV3", "STATION" });
        Assert.NotEmpty(r.StrategyReports);
    }

    [Fact]
    public void StrappingStation_ExecutesParallelPlanAtCorrectPosition()
    {
        var e = new SimulationEngine(SampleProjects.CreateStandardLine());
        while (e.Step())
        {
            if (e.Station!.State == StationState.Strapping)
            {
                var p = e.Station.Package!;
                // Tutte e tre le legatrici al lavoro contemporaneamente, pacco con testa a 41.0 m
                Assert.Equal(41.0, p.HeadM, 6);
                Assert.Equal(3, e.Strappers.Count(s => s.Busy));
                foreach (var s in e.Strappers)
                {
                    double strapPos = p.Type.StrapPositionsMm[s.StrapIndex] / 1000.0;
                    Assert.Equal(s.PositionM, p.HeadM - strapPos, 6);
                }
                return;
            }
        }
        Assert.Fail("La stazione non ha mai avviato la legatura");
    }

    [Fact]
    public void SingleStrapperLine_StrapsSequentially()
    {
        var e = new SimulationEngine(SampleProjects.CreateSingleStrapperLine());
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason + "\n" + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning)));
        Assert.Equal(0, e.Collisions);
        Assert.All(e.Packages, p => Assert.Equal(StrapStatus.Done, p.StrapStatus));
        var plan = e.StrappingPlans["P12"].Selected;
        Assert.Equal(3, plan.Stops.Count);
        Assert.All(plan.Stops, s => Assert.Single(s.Tasks));
        Assert.Equal(30, e.Strappers[0].CompletedCycles);
    }

    [Fact]
    public void ManualStrategy_IsUsedWhenSelected()
    {
        var e = RunStandard(p =>
        {
            p.Infeed.PackageCount = 2;
            p.StrappingStation.Strategy = StrappingStrategyMode.Manual;
            p.StrappingStation.ManualPlans.Add(new ManualStrappingPlan
            {
                PackageTypeId = "P12",
                Stops =
                {
                    new ManualStrappingStop { Assignments = { new StrapAssignment { StrapperId = "L3", StrapIndex = 0 } } },
                    new ManualStrappingStop { Assignments = { new StrapAssignment { StrapperId = "L2", StrapIndex = 1 }, new StrapAssignment { StrapperId = "L1", StrapIndex = 2 } } }
                }
            });
        });
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Equal(2, e.StrappingPlans["P12"].Selected.Stops.Count);
        Assert.All(e.Packages, p => Assert.Equal(StrapStatus.Done, p.StrapStatus));
    }
}
