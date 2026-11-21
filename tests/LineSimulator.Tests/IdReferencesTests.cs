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
        var stop = p.IntermediateStops.First();
        string oldId = stop.Id;
        string suffix = TransferEntryRule.TailKeySuffix;
        var linked = p.Photocells.Where(x => x.CommandsStopId == oldId || x.CommandsStopId == oldId + suffix).ToList();
        Assert.NotEmpty(linked);
        // un Id non progressivo (come dopo una modifica a mano): la rinumerazione lo riporta a FMn e le fotocellule lo seguono
        stop.Id = "FERMO_X";
        foreach (var pc in linked) pc.CommandsStopId = pc.CommandsStopId.Replace(oldId, "FERMO_X", StringComparison.Ordinal);
        SimulationPreparation.RenumberIds(p);
        Assert.NotEqual("FERMO_X", stop.Id);
        foreach (var pc in linked)
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
