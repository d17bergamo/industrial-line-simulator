using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Riferimenti tra componenti: rinumerazione dei fermi e cambio di Id (revisore C1 e C2, 2026-11-21).</summary>
public class IdReferencesTests
{
    [Fact]
    public void Renumber_KeepsThePhotocellsOfTheStops()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        _ = new SimulationEngine(p);                                         // crea i sensori automatici dei fermi
        string suffix = TransferEntryRule.TailKeySuffix;
        var stops = p.IntermediateStops.OrderBy(x => x.PositionMm).ToList();
        Assert.True(stops.Count >= 2);
        // ogni fotocellula si ricorda il fermo (oggetto) a cui è collegata
        var link = p.Photocells.Where(x => !string.IsNullOrEmpty(x.CommandsStopId))
            .Select(pc => (pc, stop: stops.First(s => pc.CommandsStopId == s.Id || pc.CommandsStopId == s.Id + suffix))).ToList();
        Assert.NotEmpty(link);
        // numeri progressivi in ordine inverso (come dopo una modifica a mano): la rinumerazione li rimette in ordine
        var ids = stops.Select(s => s.Id).ToList();
        var map = new Dictionary<string, string>();
        for (int i = 0; i < stops.Count; i++) map[ids[i]] = ids[stops.Count - 1 - i];
        foreach (var (pc, stop) in link)
            pc.CommandsStopId = map[stop.Id] + (pc.CommandsStopId.EndsWith(suffix, StringComparison.Ordinal) ? suffix : "");
        for (int i = 0; i < stops.Count; i++) stops[i].Id = map[ids[i]];
        SimulationPreparation.RenumberIds(p);
        foreach (var (pc, stop) in link)
            Assert.True(pc.CommandsStopId == stop.Id || pc.CommandsStopId == stop.Id + suffix, $"{pc.Id}: {pc.CommandsStopId} non segue il fermo {stop.Id}");
    }

    [Fact]
    public void RenameId_UpdatesEveryReference()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        _ = new SimulationEngine(p);
        var strapModule = StrappingAreaRules.StrappingModule(p)!;
        string old = strapModule.Id;
        int controlled = p.Photocells.Count(x => x.ControlledInverterId == old);
        Assert.True(controlled > 0);
        Assert.Equal(old, p.StrappingStation.InverterId);
        Assert.Null(ProjectEditing.RenameId(p, strapModule, "LEGATURA"));
        Assert.Equal("LEGATURA", strapModule.Id);
        Assert.Equal("LEGATURA", p.StrappingStation.InverterId);
        Assert.Equal(controlled, p.Photocells.Count(x => x.ControlledInverterId == "LEGATURA"));
        Assert.DoesNotContain(p.Photocells, x => x.ControlledInverterId == old);
        // e la simulazione parte e si completa con il nuovo nome
        p.Infeed.PackageCount = 3;
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
    }

    [Fact]
    public void RenameId_RefusesEmptyAndDuplicateIds()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var sections = p.Sections.OrderBy(s => s.StartMm).ToList();
        Assert.NotNull(ProjectEditing.RenameId(p, sections[0], "  "));
        Assert.NotNull(ProjectEditing.RenameId(p, sections[0], sections[1].Id));
        Assert.Null(ProjectEditing.RenameId(p, sections[0], sections[0].Id));
    }
}
