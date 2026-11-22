using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>
/// Preparazione automatica del ciclo: nomi progressivi, collegamenti, stazione che segue le legatrici
/// e legatura obbligatoria con legatrici abilitate.
/// </summary>
public class CyclePreparationTests
{
    private static string Diag(SimulationEngine e) =>
        $"{e.FinishReason} t={e.Time:0.0}\n" + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(30));

    [Fact]
    public void Samples_NeedNoCorrections()
    {
        foreach (var (name, factory) in SampleProjects.All)
        {
            var r = SimulationPreparation.Prepare(factory());
            Assert.False(r.HasErrors, name + ": " + string.Join(" | ", r.Errors));
            Assert.False(r.Changed, name + ": " + string.Join(" | ", r.Changes));
        }
    }

    [Fact]
    public void DuplicateAndUnorderedNames_AreRenumberedAlongTheLine_AndReferencesFollow()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Strappers[0].Id = "L3";   // 30,5 m
        p.Strappers[1].Id = "L1";   // 35 m
        p.Strappers[2].Id = "";     // 39,5 m
        p.Photocells.Single(x => x.Id == "FC3").Id = "FC2";   // duplicato
        p.Rules.Add(new LogicRule { Id = "R1", Action = RuleAction.SetSignal, SignalName = "PRIMA", ConditionExpression = "STR(L3)" });
        p.StrappingStation.ManualPlansText = "P12: L3#3 ; L1#2 ; L3#1";

        var r = SimulationPreparation.Prepare(p);
        Assert.False(r.HasErrors, string.Join(" | ", r.Errors));
        Assert.Equal(new[] { "L1", "L2", "L3" }, p.Strappers.OrderBy(s => s.PositionMm).Select(s => s.Id));
        Assert.Equal(p.Photocells.Count, p.Photocells.Select(x => x.Id).Distinct().Count());
        Assert.Equal(new[] { "FC1", "FC2", "FC3", "FC4", "FC5" },
            p.Photocells.Where(x => !x.IsEntryPhotocell && x.Id.Length > 2 && x.Id.StartsWith("FC", StringComparison.Ordinal) && char.IsDigit(x.Id[2])).OrderBy(x => x.PositionMm).Select(x => x.Id));
        // La regola e il piano manuale seguono la legatrice che era "L3" (la prima lungo la linea, ora L1)
        Assert.Equal("STR(L1)", p.Rules.Single().ConditionExpression);
        Assert.Equal("P12: L1#3 ; L2#2 ; L1#1", p.StrappingStation.ManualPlansText);
        Assert.Contains(r.Changes, c => c.StartsWith("Legatrici rinominati", StringComparison.Ordinal));

        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
    }

    [Fact]
    public void MovingStrappersToAnotherModule_StationFollows_AndPackagesAreStrapped()
    {
        var p = LegacySamples.CreateFullProcessLine();
        // Le tre legatrici vengono spostate dal modulo M3 al modulo M2
        p.Strappers[0].PositionMm = 16500;
        p.Strappers[1].PositionMm = 21000;
        p.Strappers[2].PositionMm = 25500;

        var r = SimulationPreparation.Prepare(p);
        Assert.False(r.HasErrors, string.Join(" | ", r.Errors));
        Assert.Equal("M2", p.StrappingStation.InverterId);
        Assert.Equal(16050, p.BundleClamp.PositionMm, 6);   // 450 mm a monte della prima macchina
        Assert.Equal(16050, p.Compactor.PositionMm, 6);
        Assert.True(p.StrappingEncoder.PositionMm < 16500 && p.StrappingEncoder.PositionMm > 13000);
        var refPc = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingReference);
        Assert.True(refPc.PositionMm < p.StrappingEncoder.PositionMm);

        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(0, e.Collisions);
        Assert.Equal(p.Infeed.PackageCount, e.Packages.Count(k => k.State == PackageState.Exited));
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
    }

    [Fact]
    public void StrappersOnTwoModules_AreJoinedInOneCommandZone()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Strappers[0].PositionMm = 25000;   // modulo M2 (13-26 m); le altre restano in M3
        var r = SimulationPreparation.Prepare(p);
        var topo = new LineTopology(p);
        Assert.Same(topo.Zone("M2"), topo.Zone("M3"));
        Assert.Same(topo.Zone(p.StrappingStation.InverterId), topo.ZoneAt(25.0));
        Assert.Contains(r.Changes, c => c.Contains("unica zona di comando", StringComparison.Ordinal));
    }

    [Fact]
    public void AllStrappersDisabled_PackagesGoToTransferWithoutStrapping()
    {
        var p = LegacySamples.CreateFullProcessLine();
        p.Infeed.PackageCount = 3;
        foreach (var s in p.Strappers) s.IsEnabled = false;
        var e = new SimulationEngine(p);
        Assert.False(p.StrappingStation.IsEnabled);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.NotRequired, k.StrapStatus));
        Assert.All(e.Packages, k => Assert.Equal(PackageState.Exited, k.State));
    }

    [Fact]
    public void StrappersEnabled_UnstrappablePackage_DoesNotStart()
    {
        // Con la commutazione automatica attiva la legatura di coda può proseguire sulla via del trasferitore (B10, 2026-11-21):
        // il caso è "non legabile" solo con la commutazione automatica disattivata
        var p = LegacySamples.CreateStandardLine();
        p.Simulation.AutoReleaseInverters = false;
        foreach (var s in p.Strappers) s.PositionMm += 2000;   // il pacco da 12 m non è più legabile nella zona M3
        var r = SimulationPreparation.Prepare(LegacySamples.CreateStandardLine().Also(q =>
        {
            q.Simulation.AutoReleaseInverters = false;
            foreach (var s in q.Strappers) s.PositionMm += 2000;
        }));
        Assert.Contains(r.Errors, x => x.Contains("deve essere legato", StringComparison.Ordinal) && x.Contains("P12", StringComparison.Ordinal));
        var e = new SimulationEngine(p);
        Assert.True(e.IsFinished);
        Assert.False(e.CompletedNormally);
        Assert.Contains(e.Events, x => x.Type == SimEventType.ConfigurationError && x.Severity == EventSeverity.Error && x.Message.Contains("deve essere legato", StringComparison.Ordinal));
        Assert.Empty(e.Packages);
    }

    [Fact]
    public void StrappersEnabled_PackageWithoutStraps_DoesNotStart()
    {
        var p = LegacySamples.CreateStandardLine();
        p.PackageTypes[0].StrapPositionsMm.Clear();
        var r = SimulationPreparation.Prepare(p);
        Assert.Contains(r.Errors, x => x.Contains("non ha legature", StringComparison.Ordinal));
    }

    [Fact]
    public void WrongLinks_AreReconnected()
    {
        var p = LegacySamples.CreateStandardLine();
        p.StrappingStation.InverterId = "M1";
        p.Photocells.Single(x => x.Id == "FC2").ControlledInverterId = "INESISTENTE";
        var r = SimulationPreparation.Prepare(p);
        Assert.False(r.HasErrors, string.Join(" | ", r.Errors));
        Assert.Equal("M3", p.StrappingStation.InverterId);
        Assert.Equal("M2", p.Photocells.Single(x => x.Id == "FC2").ControlledInverterId);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
    }
}
