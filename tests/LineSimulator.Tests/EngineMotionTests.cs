using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

public class EngineMotionTests
{
    [Fact]
    public void SingleZone_IsValid()
    {
        var r = ProjectValidator.Validate(TestLines.SingleZone());
        Assert.False(r.HasErrors, string.Join("\n", r.Errors));
    }

    [Fact]
    public void Acceleration_PositionAndVelocityMatchKinematics()
    {
        var e = new SimulationEngine(TestLines.SingleZone());
        e.RunUntil(1.0);
        var p = e.Packages.Single();
        // a = 0.5 m/s² da fermo: v = a·t = 0.5 ; s = a·t²/2 = 0.25
        Assert.Equal(0.5, p.Velocity, 6);
        Assert.Equal(12.0 + 0.25, p.HeadM, 6);
    }

    [Fact]
    public void ConstantSpeed_AfterRampReachesHighSpeed()
    {
        var e = new SimulationEngine(TestLines.SingleZone());
        e.RunUntil(5.0);
        var p = e.Packages.Single();
        Assert.Equal(0.6, p.Velocity, 9);
        // 1.2 s di rampa (0.36 m) + 3.8 s a 0.6 m/s
        Assert.Equal(12.0 + 0.36 + 3.8 * 0.6, p.HeadM, 4);
    }

    [Fact]
    public void PhotocellStop_StopsWithinResponsePlusBrakingDistance()
    {
        var proj = TestLines.SingleZone();
        proj.Photocells.Add(new PhotocellConfig
        {
            Id = "FCS", Name = "Arresto", PositionMm = 20000, ResponseTimeMs = 20,
            ControlledInverterId = "INV1", Action = PhotocellAction.Stop
        });
        var e = new SimulationEngine(proj);
        e.RunUntil(25);
        var p = e.Packages.Single();
        Assert.True(p.IsStopped);
        double expected = 20.0 + Kinematics.StoppingDistance(0.6, 0.02, 0.6);
        // tolleranza: ritardo di scansione massimo 2 passi a 0.6 m/s
        Assert.InRange(p.HeadM, expected - 0.002, expected + 0.02);
        Assert.Equal(InverterMode.Stop, e.Zone("INV1")!.Mode);
    }

    [Fact]
    public void Braking_DecelerationEqualsConfiguredRamp()
    {
        var proj = TestLines.SingleZone();
        proj.Photocells.Add(new PhotocellConfig { Id = "FCS", PositionMm = 20000, ResponseTimeMs = 0, ControlledInverterId = "INV1", Action = PhotocellAction.Stop });
        var e = new SimulationEngine(proj);
        var p0 = (PackageRuntime?)null;
        double? vPrev = null; double maxDecel = 0;
        while (e.Time < 25 && !e.IsFinished)
        {
            e.Step();
            p0 ??= e.Packages.FirstOrDefault();
            if (p0 == null) continue;
            if (vPrev.HasValue) maxDecel = Math.Max(maxDecel, (vPrev.Value - p0.Velocity) / e.Dt);
            vPrev = p0.Velocity;
        }
        Assert.InRange(maxDecel, 0.6 - 1e-6, 0.6 + 1e-6);
    }

    [Fact]
    public void SlowDownPhotocell_SwitchesToLowSpeed_AndLimitsImpact()
    {
        var proj = TestLines.SingleZone();
        proj.Photocells.Add(new PhotocellConfig { Id = "FCR", PositionMm = 26000, ControlledInverterId = "INV1", Action = PhotocellAction.SlowDown });
        var e = new SimulationEngine(proj);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Equal(0, e.ExcessiveImpacts);
        Assert.Contains(e.Recorder.Tracks.Single(t => t.Key == "INV:INV1").Segments, s => s.State == CycleState.Low);
    }

    [Fact]
    public void WithoutSlowDown_ExcessiveImpactIsReported()
    {
        var e = new SimulationEngine(TestLines.SingleZone());
        e.RunToCompletion();
        Assert.True(e.ExcessiveImpacts > 0);
        Assert.Contains(e.Events, x => x.Type == SimEventType.ExcessiveImpact);
    }

    [Fact]
    public void Rule_SetLowSpeed_AppliesWhileConditionTrue()
    {
        var proj = TestLines.SingleZone();
        proj.Rules.Add(new LogicRule
        {
            Id = "R1", Name = "Bassa sempre", TargetInverterId = "INV1", Action = RuleAction.SetLowSpeed,
            Condition = new PackageHeadInRangeCondition { FromMm = 0, ToMm = 100000 }
        });
        var e = new SimulationEngine(proj);
        e.RunUntil(10);
        Assert.Equal(0.15, e.Packages.Single().Velocity, 9);
        Assert.Equal(SpeedLevel.Low, e.Zone("INV1")!.Level);
    }

    [Fact]
    public void Rule_Signals_AreVisibleOnNextScan_WithAndOrNot()
    {
        var proj = TestLines.SingleZone();
        proj.Rules.Add(new LogicRule { Id = "R1", Action = RuleAction.SetSignal, SignalName = "OK", Condition = new ConstantCondition { Value = true } });
        proj.Rules.Add(new LogicRule
        {
            Id = "R2", TargetInverterId = "INV1", Action = RuleAction.StopInverter,
            Condition = new AndCondition { Children = { new SignalCondition { SignalName = "OK" }, new NotCondition { Child = new TransferAtHomeCondition() } } }
        });
        proj.Rules.Add(new LogicRule
        {
            Id = "R3", TargetInverterId = "INV1", Action = RuleAction.SetLowSpeed,
            Condition = new OrCondition { Children = { new ConstantCondition { Value = false }, new SignalCondition { SignalName = "OK" } } }
        });
        var e = new SimulationEngine(proj);
        e.Step();
        Assert.Contains("OK", e.ActiveSignals);
        e.RunUntil(5);
        // R2 falsa (trasferitore a casa) → marcia; R3 vera → bassa
        Assert.Equal(SpeedLevel.Low, e.Zone("INV1")!.Level);
        Assert.True(e.Zone("INV1")!.Running);
    }

    [Fact]
    public void StopperAlignment_TakesConfiguredTime_ThenTransferSequence()
    {
        var e = new SimulationEngine(TestLines.SingleZone());
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);

        double contact = TestLines.First(e, SimEventType.StopperContact).TimeS;
        double aligned = TestLines.First(e, SimEventType.AlignmentCompleted).TimeS;
        Assert.InRange(aligned - contact, 2.5 - 1e-6, 2.5 + 3 * e.Dt);

        var phases = e.Events.Where(x => x.Type == SimEventType.TransferPhase).ToList();
        var names = phases.Select(x => x.Message).ToList();
        Assert.Equal(new[]
        {
            "Trasferitore: sollevamento", "Trasferitore: traslazione", "Trasferitore: abbassamento",
            "Trasferitore: deposito", "Trasferitore: ritorno", "Trasferitore: in posizione"
        }, names);
        double liftStart = phases[0].TimeS;
        Assert.InRange(liftStart - aligned, 0, 2 * e.Dt);
        double exit = e.Packages.Single().ExitTimeS!.Value;
        // sollevamento 2 + traslazione 3 m / 0.5 = 6 + abbassamento 2 + deposito 1 = 11 s
        Assert.InRange(exit - liftStart, 11 - 1e-6, 11 + 2 * e.Dt);
        double home = phases[^1].TimeS;
        Assert.InRange(home - exit, 6 - 1e-6, 6 + 2 * e.Dt);
    }

    [Fact]
    public void Package_StaysAgainstStopperDuringAlignment()
    {
        var e = new SimulationEngine(TestLines.SingleZone());
        while (!e.IsFinished && e.Stopper.State != StopperState.Aligning) e.Step();
        var p = e.Stopper.Package!;
        for (int i = 0; i < 150; i++)
        {
            e.Step();
            Assert.Equal(e.Stopper.PositionM, p.HeadM, 9);
            Assert.Equal(0, p.Velocity, 9);
            // i rulli restano accesi alla velocità di allineamento
            Assert.Equal(InverterMode.Align, e.Zone("INV1")!.Mode);
        }
        Assert.True(e.Zone("INV1")!.ActualSpeed > 0.09);
    }

    [Fact]
    public void Deadlock_IsDetectedAndReported()
    {
        var proj = TestLines.SingleZone();
        proj.Simulation.DeadlockTimeoutS = 5;
        proj.Rules.Add(new LogicRule { Id = "R1", TargetInverterId = "INV1", Action = RuleAction.StopInverter, Condition = new ConstantCondition { Value = true } });
        var e = new SimulationEngine(proj);
        e.RunToCompletion();
        Assert.True(e.Deadlocked);
        Assert.False(e.CompletedNormally);
        Assert.Contains(e.Events, x => x.Type == SimEventType.Deadlock && x.Severity == EventSeverity.Error);
    }

    [Fact]
    public void InvalidConfiguration_IsNotSimulated()
    {
        var proj = TestLines.SingleZone();
        proj.Inverters[0].HighSpeed = 0;
        var e = new SimulationEngine(proj);
        Assert.True(e.IsFinished);
        Assert.Contains(e.Events, x => x.Type == SimEventType.ConfigurationError);
    }
}
