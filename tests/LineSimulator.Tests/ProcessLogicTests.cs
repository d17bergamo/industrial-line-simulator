using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Infrastructure;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>
/// Scenari delle logiche di processo: fotocellula di ingresso, arresto automatico degli inverter,
/// legatrice KNCA 7/800 con stringifascio KNOF e compattatore, rigettatrice SMA, fermi intermedi
/// con intestatura di testa/coda, trasferitore multi-pacco e bilici di pesatura.
/// </summary>
public class ProcessLogicTests
{
    private static LineProject Full(Action<LineProject>? tweak = null)
    {
        var p = SampleProjects.CreateFullProcessLine();
        tweak?.Invoke(p);
        return p;
    }

    private static string Diag(SimulationEngine e) =>
        $"{e.FinishReason} t={e.Time:0.0}\n" + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(40));

    /// <summary>Esegue fino al termine verificando a ogni passo gli invarianti di processo.</summary>
    private static SimulationEngine RunChecked(LineProject p, Action<SimulationEngine>? perStep = null)
    {
        var e = new SimulationEngine(p);
        while (e.Step())
        {
            CheckInvariants(e);
            perStep?.Invoke(e);
        }
        CheckInvariants(e);
        return e;
    }

    private static void CheckInvariants(SimulationEngine e)
    {
        // Nessuna sovrapposizione tra pacchi sui rulli
        var onRollers = e.Packages.Where(x => x.OnRollers && x.ExitTimeS == null).OrderByDescending(x => x.HeadM).ToList();
        for (int i = 1; i < onRollers.Count; i++)
            Assert.True(onRollers[i - 1].TailM >= onRollers[i].HeadM - 1e-6,
                $"t={e.Time:0.00}: sovrapposizione {onRollers[i - 1].Id}/{onRollers[i].Id}");

        // Legatura solo con stringifascio chiuso, e in posizione corretta sotto la legatrice
        foreach (var s in e.Strappers.Where(s => s.Busy && !s.Faulted && s.State is StrapperState.Preparing or StrapperState.Strapping))
        {
            if (e.BundleClamp != null) Assert.True(e.BundleClamp.IsClosed, $"t={e.Time:0.00}: {s.Id} lega con stringifascio {e.BundleClamp.State}");
            var p = s.Package!;
            Assert.Equal(s.PositionM, p.HeadM - p.Type.StrapPositionsMm[s.StrapIndex] / 1000.0, 5);
            Assert.True(p.IsStopped);
        }

        // Pacco in stazione fermo finché stringifascio o compattatore non sono aperti
        var st = e.Station;
        if (st?.Package != null && st.Package.OnRollers)
        {
            bool devicesClosed = (e.BundleClamp != null && !e.BundleClamp.IsOpen) || (e.Compactor != null && !e.Compactor.IsOpen);
            if (devicesClosed)
            {
                Assert.True(st.Package.IsStopped, $"t={e.Time:0.00}: {st.Package.Id} si muove con dispositivi chiusi");
                Assert.Equal(0, st.Zone.ActualSpeed, 9);
            }
        }

        // Arresto automatico solo senza pacchi in zona, nella sezione precedente o in arrivo
        foreach (var z in e.Zones.Where(z => z.AutoStopped && !z.AssignedPackages.Any()))
        {
            foreach (var p in e.Packages.Where(x => x.OnRollers && x.ExitTimeS == null))
            {
                bool inside = p.HeadM > z.Info.StartM + 1e-9 && p.TailM < z.Info.EndM - 1e-9;
                Assert.False(inside && !z.Info.Kind.Equals(ZoneKind.Stopper), $"t={e.Time:0.00}: {z.Id} in arresto automatico con {p.Id} in zona");
            }
        }
    }

    // ------------------------------------------------------------------ Configurazione

    [Fact]
    public void FullProcessLine_IsValid()
    {
        var r = ProjectValidator.Validate(Full());
        Assert.False(r.HasErrors, string.Join("\n", r.Errors));
    }

    [Fact]
    public void EntryPhotocell_IsMandatory()
    {
        var p = TestLines.SingleZone();
        p.Photocells.RemoveAll(x => x.IsEntryPhotocell);
        var r = ProjectValidator.Validate(p);
        Assert.Contains(r.Errors, x => x.Code == "PC_ENTRY");
        var e = new SimulationEngine(p);
        Assert.True(e.IsFinished);
        Assert.False(e.CompletedNormally);
    }

    [Fact]
    public void SchemaV1Project_GetsEntryPhotocellOnLoad()
    {
        var p = SampleProjects.CreateStandardLine();
        p.Photocells.RemoveAll(x => x.IsEntryPhotocell);
        string json = ProjectSerializer.Serialize(p).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1");
        var loaded = ProjectSerializer.Deserialize(json);
        Assert.Equal(LineProject.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Single(loaded.Photocells, x => x.IsEntryPhotocell);
        Assert.False(ProjectValidator.Validate(loaded).HasErrors);
        Assert.False(loaded.BundleClamp.IsEnabled);
        Assert.False(loaded.Rejector.IsEnabled);
    }

    [Fact]
    public void FullProcessLine_RoundTripsThroughJson()
    {
        var p = Full();
        var copy = ProjectSerializer.Clone(p);
        Assert.Equal(ProjectSerializer.Serialize(p), ProjectSerializer.Serialize(copy));
        Assert.True(copy.BundleClamp.IsEnabled);
        Assert.Equal(2, copy.Scales.Count);
        Assert.Single(copy.IntermediateStops);
        Assert.Equal("5", copy.Rejector.RejectPackageNumbers);
    }

    [Fact]
    public void DeviceCondition_ParsesAndFormats()
    {
        var c = ConditionText.Parse("DEV(KNOF,Closed) & !DEV(SMA)");
        Assert.Equal("DEV(KNOF,Closed) & !DEV(SMA)", ConditionText.Format(c));
    }

    // ------------------------------------------------------------------ Scenari

    [Fact]
    public void SinglePackage_IdentifiedAtEntry_AndCompletesWholeProcess()
    {
        var e = RunChecked(Full(p => p.Infeed.PackageCount = 1));
        Assert.True(e.CompletedNormally, Diag(e));
        var pk = e.Packages.Single();
        Assert.True(pk.Identified);
        Assert.Contains(e.Events, x => x.Type == SimEventType.PackageIdentified && x.PackageId == pk.Id && x.ComponentId == "FC0");
        Assert.NotNull(pk.MeasuredLengthM);
        Assert.InRange(pk.MeasuredLengthM!.Value, 6.0 - 1e-6, 6.0 + 0.02);
        Assert.Equal(PackageState.Exited, pk.State);
        Assert.Equal(StrapStatus.Done, pk.StrapStatus);
        Assert.Equal(3, pk.StrapsDone);
        Assert.Equal(1500.0, pk.MeasuredWeightKg!.Value);
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.DeviceAlarms);
    }

    [Fact]
    public void ConsecutivePackages_MixedLengths_AllProcessedWithoutCollisions()
    {
        var e = RunChecked(Full());
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.SafetyViolations);
        Assert.Equal(0, e.ZoneOverfills);
        Assert.Equal(0, e.PositioningErrors);
        Assert.False(e.Deadlocked);
        Assert.Equal(1, e.RejectedPackages);
        var exited = e.Packages.Where(p => p.State == PackageState.Exited).ToList();
        Assert.Equal(7, exited.Count);
        Assert.All(exited, p => Assert.Equal(StrapStatus.Done, p.StrapStatus));
        Assert.All(exited, p => Assert.Equal(p.WeightKg, p.MeasuredWeightKg!.Value));
        Assert.Equal(new[] { "P6", "P12", "P4" }.OrderBy(x => x), exited.Select(p => p.Type.Id).Distinct().OrderBy(x => x));
        Assert.Equal(1480.0, e.Packages.Single(p => p.Sequence == 2).MeasuredWeightKg!.Value);
        var r = e.ComputeResults();
        Assert.Equal(1, r.RejectedPackages);
        Assert.Equal(7, r.WeighedPackages);
    }

    [Fact]
    public void TwoSixMeterPackages_In12mAccumulation_AlignedAndLiftedTogether()
    {
        var p = Full(x =>
        {
            x.Infeed.TypeSequence = new List<string> { "P6" };
            x.Infeed.PackageCount = 2;
            x.Rejector.RejectPackageNumbers = "";
        });
        PackageRuntime[]? lifted = null;
        var e = RunChecked(p, en =>
        {
            if (lifted == null && en.Transfer.State == TransferState.Lifting)
            {
                lifted = en.Transfer.Packages.ToArray();
                Assert.All(lifted, k => Assert.Equal(PackageState.Aligned, k.State));
            }
        });
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.NotNull(lifted);
        Assert.Equal(2, lifted!.Length);
        var first = lifted.Single(k => k.Sequence == 1);
        var second = lifted.Single(k => k.Sequence == 2);
        // Coordinate: il primo allo stopper finale, il secondo al fermo intermedio con intestatura di testa
        Assert.Equal(56.5, first.HeadM, 6);
        Assert.InRange(second.HeadM, 50.1 - 0.003, 50.1 + 1e-6);
        Assert.True(second.HeadM <= first.TailM - e.Project.Simulation.MinSafetyDistanceMm / 1000.0 + 1e-6);
        Assert.Equal(1, e.Transfer.Cycles);
        Assert.Contains(e.Events, x => x.Type == SimEventType.StopPhase && x.Message.Contains("intestatura di testa"));
        // Depositati e pesati sui due bilici
        Assert.Equal("B2", first.ScaleId);
        Assert.Equal("B1", second.ScaleId);
        Assert.Equal(1480.0, second.MeasuredWeightKg!.Value);
        Assert.Equal(0, e.Collisions);
    }

    [Fact]
    public void TailAlignment_PutsTailOnReference()
    {
        var p = Full(x =>
        {
            x.Infeed.TypeSequence = new List<string> { "P6" };
            x.Infeed.PackageCount = 2;
            x.Rejector.RejectPackageNumbers = "";
            x.IntermediateStops[0].Reference = StopReference.Tail;
            x.IntermediateStops[0].PositionMm = 44100;
        });
        double? tailAtLift = null;
        var e = RunChecked(p, en =>
        {
            if (tailAtLift == null && en.Transfer.State == TransferState.Lifting)
                tailAtLift = en.Transfer.Packages.Single(k => k.Sequence == 2).TailM;
        });
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.NotNull(tailAtLift);
        Assert.InRange(tailAtLift!.Value, 44.1 - 0.003, 44.1 + 1e-6);
        Assert.Contains(e.Events, x => x.Type == SimEventType.StopPhase && x.Message.Contains("intestatura di coda"));
        Assert.Equal(1, e.Transfer.Cycles);
    }

    [Fact]
    public void KncaStrapper_GoesThroughFullProcessStateSequence()
    {
        var seen = new List<StrapperProcessState>();
        var e = RunChecked(Full(x => x.Infeed.PackageCount = 1), en =>
        {
            var s = en.Strappers.Single(k => k.Id == "L1").ProcessState;
            if (seen.Count == 0 || seen[^1] != s) seen.Add(s);
        });
        Assert.True(e.CompletedNormally, Diag(e));
        var expected = new[]
        {
            StrapperProcessState.WaitingPackage, StrapperProcessState.Positioning, StrapperProcessState.PackageInPosition,
            StrapperProcessState.WaitingClampConsent, StrapperProcessState.StrappingCycle, StrapperProcessState.CycleCompleted,
            StrapperProcessState.Unloading
        };
        int k = 0;
        foreach (var s in seen) if (k < expected.Length && s == expected[k]) k++;
        Assert.True(k == expected.Length, "Sequenza osservata: " + string.Join(" → ", seen));
        Assert.Equal(StrapperProcessState.Available, e.Strappers.Single(x => x.Id == "L3").ProcessState);
    }

    [Fact]
    public void BundleClamp_ClosesBeforeStrapping_StaysClosed_OpensAfter()
    {
        var e = RunChecked(Full(x => x.Infeed.PackageCount = 1), en =>
        {
            // Apertura solo dopo la fine della legatura di tutte le legatrici della fermata
            if (en.BundleClamp!.State == ClampState.Opening)
                Assert.DoesNotContain(en.Strappers, s => s.State is StrapperState.Preparing or StrapperState.Strapping);
        });
        Assert.True(e.CompletedNormally, Diag(e));
        var ev = e.Events;
        double closed = ev.First(x => x.Type == SimEventType.BundleClampPhase && x.Message.Contains("conferma di chiusura")).TimeS;
        double command = ev.First(x => x.Type == SimEventType.BundleClampPhase && x.Message.Contains("comando di chiusura")).TimeS;
        double firstStrap = ev.First(x => x.Type == SimEventType.StrapperPhase && x.Message.Contains("preparazione")).TimeS;
        double opened = ev.First(x => x.Type == SimEventType.BundleClampPhase && x.Message.Contains("conferma di apertura")).TimeS;
        // chiusura 1.5 s + conferma 0.2 s
        Assert.InRange(closed - command, 1.7 - 1e-6, 1.7 + 3 * e.Dt);
        Assert.True(firstStrap >= closed - 1e-9);
        Assert.True(opened > firstStrap);
        Assert.Equal(2, e.BundleClamp!.Cycles); // una chiusura per fermata (2 fermate per il pacco da 6 m)
    }

    [Fact]
    public void BundleClampFault_BlocksStrappingUntilReset()
    {
        var e = RunChecked(Full(x =>
        {
            x.Infeed.PackageCount = 1;
            x.BundleClamp.SimulatedFaultAtCycle = 1;
            x.BundleClamp.FaultResetTimeS = 20;
        }));
        Assert.True(e.CompletedNormally, Diag(e));
        var ev = e.Events;
        double command = ev.First(x => x.Type == SimEventType.BundleClampPhase && x.Message.Contains("comando di chiusura")).TimeS;
        var alarm = ev.First(x => x.Type == SimEventType.DeviceAlarm && x.ComponentId == "KNOF");
        double firstStrap = ev.First(x => x.Type == SimEventType.StrapperPhase && x.Message.Contains("preparazione")).TimeS;
        Assert.InRange(alarm.TimeS - command, 10 - 1e-6, 10 + 3 * e.Dt);         // timeout conferma
        Assert.True(firstStrap >= command + 10 + 20 + 1.7 - 3 * e.Dt);           // ripristino + nuova chiusura
        Assert.Equal(1, e.BundleClamp!.Faults);
        Assert.True(e.DeviceAlarms >= 1);
    }

    [Fact]
    public void StrapperFault_StopsCycleThenRestarts()
    {
        var e = RunChecked(Full(x =>
        {
            x.Infeed.PackageCount = 1;
            x.Strappers[0].SimulatedFaultAtCycle = 1;
            x.Strappers[0].FaultResetTimeS = 15;
        }));
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Contains(e.Events, x => x.Type == SimEventType.StrapperFault && x.Severity == EventSeverity.Error && x.ComponentId == "L1");
        Assert.Contains(e.Events, x => x.Type == SimEventType.StrapperFault && x.Message.Contains("ripristinato"));
        Assert.Contains(e.Recorder.Tracks.Single(t => t.Key == "STR:L1").Segments, s => s.State == CycleState.Fault);
        Assert.Equal(3, e.Packages.Single().StrapsDone);
    }

    [Fact]
    public void Compactor_ClosesBeforeStrapping_WithConveyorStopped()
    {
        bool sawClosedWhileStopped = false;
        var e = RunChecked(Full(x => x.Infeed.PackageCount = 1), en =>
        {
            if (en.Compactor!.State is ClampState.Closing or ClampState.Closed)
            {
                Assert.Equal(InverterMode.Stop, en.Station!.Zone.Mode);
                sawClosedWhileStopped = true;
            }
        });
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.True(sawClosedWhileStopped);
        var ev = e.Events;
        double compacted = ev.First(x => x.Type == SimEventType.CompactorPhase && x.Message.Contains("consenso alla legatura")).TimeS;
        double knofCmd = ev.First(x => x.Type == SimEventType.BundleClampPhase && x.Message.Contains("comando di chiusura")).TimeS;
        double firstStrap = ev.First(x => x.Type == SimEventType.StrapperPhase && x.Message.Contains("preparazione")).TimeS;
        Assert.True(compacted <= knofCmd + 1e-9);
        Assert.True(compacted < firstStrap);
    }

    [Fact]
    public void Rejector_CompleteRejectSequence()
    {
        var p = Full(x => x.Infeed.PackageCount = 5);
        bool zoneStoppedDuringEject = true;
        double? headInPosition = null;
        var e = RunChecked(p, en =>
        {
            var r = en.Rejector!;
            if (r.State == RejectorState.Ejecting && en.Zone("INV2")!.ActualSpeed > 1e-9) zoneStoppedDuringEject = false;
            if (r.State == RejectorState.InPosition && headInPosition == null) headInPosition = r.Package!.HeadM;
        });
        Assert.True(e.CompletedNormally, Diag(e));
        var rejected = e.Packages.Single(k => k.Sequence == 5);
        Assert.Equal(PackageState.Rejected, rejected.State);
        Assert.NotNull(rejected.RejectedTimeS);
        Assert.Null(rejected.ExitTimeS);
        Assert.Equal(0, rejected.StrapsDone);
        Assert.True(zoneStoppedDuringEject);
        Assert.NotNull(headInPosition);
        Assert.InRange(headInPosition!.Value, 23.5 - 0.005, 23.5 + 1e-6); // pacco centrato nel campo di spinta
        string[] steps =
        {
            "identificato come pacco da rigettare", "ciclo di rigetto abilitato", "arresto e posizionamento", "fermo in posizione di rigetto",
            "comando di espulsione", "espulsione di P005 confermata", "spintore tornato", "consenso alla ripartenza"
        };
        var msgs = e.Events.Where(x => x.Type == SimEventType.RejectorPhase && x.PackageId == "P005").Select(x => x.Message).ToList();
        int k = 0;
        foreach (var m in msgs) if (k < steps.Length && m.Contains(steps[k])) k++;
        Assert.True(k == steps.Length, string.Join("\n", msgs));
        Assert.Equal(1, e.Rejector!.Cycles);
        Assert.Equal(4, e.Packages.Count(x => x.State == PackageState.Exited));
    }

    [Fact]
    public void ShortPackage_IsWeighedAtIntermediatePosition()
    {
        double? headAtStabilizing = null;
        var e = RunChecked(Full(x =>
        {
            x.Infeed.TypeSequence = new List<string> { "P4" };
            x.Infeed.PackageCount = 1;
            x.Rejector.RejectPackageNumbers = "";
        }), en =>
        {
            var b2 = en.Scales.Single(s => s.Id == "B2");
            if (b2.State == ScaleState.Stabilizing && headAtStabilizing == null) headAtStabilizing = b2.Package!.HeadM;
        });
        Assert.True(e.CompletedNormally, Diag(e));
        var pk = e.Packages.Single();
        Assert.Equal("B2", pk.ScaleId);
        Assert.Equal(1000.0, pk.MeasuredWeightKg!.Value);
        Assert.True(pk.WeighingValid);
        Assert.True(e.Scales.Single(s => s.Id == "B2").ShortPosition);
        // Posizione intermedia = pacco centrato sul bilico: (50.3 + 56.6 + 4.0) / 2 = 55.45 m
        Assert.NotNull(headAtStabilizing);
        Assert.Equal(55.45, headAtStabilizing!.Value, 3);
        Assert.Contains(e.Events, x => x.Type == SimEventType.WeighingPhase && x.Message.Contains("posizione intermedia"));
        Assert.Contains(e.Events, x => x.Type == SimEventType.WeightAcquired && x.Message.Contains("1000 kg"));
    }

    [Fact]
    public void LongPackage_IsWeighedOnTwoScalesCombined()
    {
        var e = RunChecked(Full(x =>
        {
            x.Infeed.TypeSequence = new List<string> { "P12" };
            x.Infeed.PackageCount = 1;
            x.Rejector.RejectPackageNumbers = "";
        }));
        Assert.True(e.CompletedNormally, Diag(e));
        var pk = e.Packages.Single();
        Assert.Equal("B1+B2", pk.ScaleId);
        Assert.Equal(3000.0, pk.MeasuredWeightKg!.Value);
        Assert.True(pk.WeighingValid);
    }

    [Fact]
    public void Inverters_StopAutomatically_WhenNoPackages_AndRestartForNextPackage()
    {
        var p = SampleProjects.CreateStandardLine();
        p.Infeed.PackageCount = 2;
        p.Infeed.IntervalS = 200;
        var e = RunChecked(p);
        Assert.True(e.CompletedNormally, Diag(e));
        var inv1Stops = e.Events.Where(x => x.Type == SimEventType.AutoStop && x.ComponentId == "INV1").ToList();
        var inv1Starts = e.Events.Where(x => x.Type == SimEventType.AutoStart && x.ComponentId == "INV1").ToList();
        Assert.NotEmpty(inv1Stops);
        Assert.NotEmpty(inv1Starts);
        // Riavvio anticipato del tempo di accelerazione (0.6/0.5 = 1.2 s) rispetto all'ingresso del secondo pacco
        var restart = inv1Starts.First(x => x.TimeS > 100);
        Assert.InRange(restart.TimeS, 200 - 1.2 - 2 * e.Dt, 200 + 1e-9);
        // Il primo arresto di INV1 avviene dopo l'uscita completa del primo pacco dalla sezione precedente/zona
        var firstPkg = e.Packages.Single(k => k.Sequence == 1);
        Assert.True(inv1Stops[0].TimeS > firstPkg.EntryTimeS);
        // Al termine tutte le zone sono ferme per arresto automatico
        Assert.All(e.Zones, z => Assert.Equal(InverterMode.Stop, z.Mode));
        Assert.All(e.Zones, z => Assert.True(z.AutoStopped, z.Id + ": " + z.Reason));
    }

    [Fact]
    public void AutoStopDisabled_KeepsInverterRunning()
    {
        var p = SampleProjects.CreateStandardLine();
        p.Infeed.PackageCount = 1;
        foreach (var inv in p.Inverters) inv.AutoStopWhenEmpty = false;
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.DoesNotContain(e.Events, x => x.Type == SimEventType.AutoStop);
        Assert.True(e.Zone("INV1")!.Running);
    }

    [Fact]
    public void FullProcessLine_IsRepeatable()
    {
        var a = new SimulationEngine(Full()); a.RunToCompletion();
        var b = new SimulationEngine(Full()); b.RunToCompletion();
        Assert.Equal(a.Events.Count, b.Events.Count);
        Assert.Equal(a.Events.Select(x => (x.TimeS, x.Message)), b.Events.Select(x => (x.TimeS, x.Message)));
        Assert.Equal(a.Packages.Select(x => x.ExitTimeS), b.Packages.Select(x => x.ExitTimeS));
    }

    [Fact]
    public void Cyclogram_And3DFrames_ShareTheSameStates()
    {
        var e = new SimulationEngine(Full(x => x.Infeed.PackageCount = 2));
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        foreach (var key in new[] { "KNOF", "CMP", "SMA", "STOP:FM1", "SCALE:B1", "SCALE:B2", "STR:L1" })
            Assert.Contains(e.Recorder.Tracks, t => t.Key == key);
        // Per ogni fotogramma lo stato del bilico registrato nel ciclogramma coincide con quello del fotogramma 3D
        var track = e.Recorder.Tracks.Single(t => t.Key == "SCALE:B2");
        int checkedFrames = 0;
        foreach (var f in e.Recorder.Frames.Where(f => f.ScaleStates.Length == 2))
        {
            var seg = track.At(f.TimeS);
            if (seg == null || Math.Abs(seg.StartS - f.TimeS) < 1e-9 || Math.Abs(seg.EndS - f.TimeS) < 1e-9) continue;
            bool busyFrame = f.ScaleStates[1] != ScaleState.Idle;
            bool busyTrack = seg.State != CycleState.Idle;
            Assert.Equal(busyFrame, busyTrack);
            checkedFrames++;
        }
        Assert.True(checkedFrames > 10);
    }
}
