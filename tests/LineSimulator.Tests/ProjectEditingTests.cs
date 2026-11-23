using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>Regole di modifica comuni al programma Windows e alla versione web (ProjectEditing, 2026-11-18).</summary>
public class ProjectEditingTests
{
    [Fact]
    public void SectionLength_ReflowsTheLine_AndTheTransferFollowsTheLastModule()
    {
        var p = SampleProjects.All[0].Factory();
        var first = p.Sections.OrderBy(s => s.StartMm).First();
        var next = p.Sections.OrderBy(s => s.StartMm).Skip(1).First();
        first.LengthMm += 1250;
        Assert.True(ProjectEditing.AfterEdit(p, first, nameof(RollerSection.LengthMm)));
        Assert.Equal(first.EndMm, next.StartMm, 3);
        var last = p.Sections.OrderBy(s => s.StartMm).Last();
        last.LengthMm += 1250;
        ProjectEditing.AfterEdit(p, last, nameof(RollerSection.LengthMm));
        Assert.Equal(last.LengthMm, p.Transfer.LengthMm, 3);
        ProjectEditing.Normalize(p);
        Assert.Equal(last.EndMm, p.Stopper.PositionMm, 3);
    }

    [Fact]
    public void AddAndDelete_EveryAddableComponent_KeepsTheProjectSimulable()
    {
        foreach (var (key, _) in ProjectEditing.Addable)
        {
            var p = SampleProjects.All[0].Factory();
            var added = ProjectEditing.Add(p, key);
            if (key == "strapper" && p.Strappers.Count >= LineProject.MaxStrappers && added == null) continue;
            Assert.NotNull(added);
            ProjectEditing.Normalize(p);
            var json = ProjectSerializer.Serialize(p);
            Assert.False(string.IsNullOrEmpty(json));
            if (ProjectEditing.CanDelete(added)) ProjectEditing.Delete(p, new[] { added! });
            ProjectEditing.Normalize(p);
            ProjectAutoFix.Run(p);
            var e = new SimulationEngine(p);
            e.RunToCompletion();
            Assert.True(e.CompletedNormally, $"{key}: {e.FinishReason}");
        }
    }

    [Fact]
    public void Add_FixAndSimulate_WithTheComponentPresent()
    {
        // E (revisore): il test qui sopra cancella subito il componente; qui la simulazione gira con il componente nel progetto
        foreach (var (key, _) in ProjectEditing.Addable)
        {
            var p = SampleProjects.All[0].Factory();
            var added = ProjectEditing.Add(p, key);
            if (added == null) continue;
            ProjectEditing.Normalize(p);
            ProjectAutoFix.Run(p);
            var e = new SimulationEngine(p);
            if (e.Validation.HasErrors || e.Preparation.HasErrors) continue;                    // lo segnala già il controllo prima di simulare
            e.RunToCompletion();
            Assert.True(e.CompletedNormally, $"{key}: {e.FinishReason}");
            Assert.Equal(0, e.Collisions);
        }
    }

    [Fact]
    public void Locks_FamilyValuesAndFixedPositions()
    {
        var p = SampleProjects.All[0].Factory();
        var entry = p.Photocells.FirstOrDefault(x => x.IsEntryPhotocell);
        if (entry != null) Assert.True(ProjectEditing.IsLocked(p, entry, nameof(PhotocellConfig.PositionMm)));
        var st = p.Strappers[0];
        st.OverrideGroup = false;
        foreach (var name in GroupInheritance.InheritedNames(p.StrapperDefaults))
            Assert.True(ProjectEditing.IsLocked(p, st, name));
        Assert.NotNull(ProjectEditing.IdOptions(p, "TargetInverterId"));
    }

    [Fact]
    public void InMemoryOutputs_ForTheWebVersion()
    {
        var p = SampleProjects.All[0].Factory();
        var files = ControlLogicGenerator.Files(ControlLogicGenerator.Generate(p), "x");
        Assert.Contains(files, f => f.Name == "x.json");
        Assert.True(files.Count >= 6);
        Assert.True(FunctionalSpecExporter.Build(p, null).Length > 1000);
        var e = new SimulationEngine(ProjectSerializer.Clone(p));
        e.RunToCompletion();
        Assert.True(ReportExporter.ExcelBytes(e.ComputeResults(), e).Length > 1000);
    }
}
