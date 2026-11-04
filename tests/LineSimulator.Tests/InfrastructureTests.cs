using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;

namespace LineSimulator.Tests;

public class InfrastructureTests
{
    [Fact]
    public void Json_RoundTrip_PreservesProject()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Rules.Add(new LogicRule
        {
            Id = "R1", Name = "Esempio", TargetInverterId = "M2", Action = RuleAction.SetLowSpeed,
            Condition = new AndCondition
            {
                Children =
                {
                    new PhotocellCondition { PhotocellId = "FC2", Occupied = true },
                    new OrCondition { Children = { new GapAheadLessThanCondition { InverterId = "M2", GapMm = 2000 }, new NotCondition { Child = new TransferAtHomeCondition() } } }
                }
            }
        });
        string json = ProjectSerializer.Serialize(p);
        var q = ProjectSerializer.Deserialize(json);
        Assert.Equal(json, ProjectSerializer.Serialize(q));
        Assert.Equal(LineProject.CurrentSchemaVersion, q.SchemaVersion);
        var and = Assert.IsType<AndCondition>(q.Rules[0].Condition);
        Assert.IsType<PhotocellCondition>(and.Children[0]);
        Assert.Equal(new List<double> { 1500, 6000, 10500 }, q.PackageTypes[0].StrapPositionsMm);
        Assert.Equal(p.Sections.Select(x => (x.Id, x.HighSpeed, x.Deceleration)), q.Sections.Select(x => (x.Id, x.HighSpeed, x.Deceleration)));
    }

    [Fact]
    public void Json_NewerSchemaIsRejected()
    {
        var json = ProjectSerializer.Serialize(LegacySamples.CreateStandardLine()).Replace($"\"schemaVersion\": {LineProject.CurrentSchemaVersion}", "\"schemaVersion\": 999");
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void Duplicate_ChangesIdAndName()
    {
        var p = LegacySamples.CreateStandardLine();
        var d = ProjectSerializer.Duplicate(p);
        Assert.NotEqual(p.ProjectId, d.ProjectId);
        Assert.EndsWith("(copia)", d.Name);
    }

    [Fact]
    public void ExampleFile_LoadsAndIsValid()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Examples", "linea-standard.json");
        Assert.True(File.Exists(path), path);
        var p = new ProjectRepository(Path.GetTempPath()).Load(path);
        var r = ProjectValidator.Validate(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Errors));
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Equal(0, e.Collisions);
    }

    [Fact]
    public void Repository_SaveLoadAndAutosave()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ils_test_" + Guid.NewGuid().ToString("N"));
        var repo = new ProjectRepository(Path.Combine(dir, "auto"));
        var p = LegacySamples.CreateStandardLine();
        string file = Path.Combine(dir, "linea.json");
        repo.Save(p, file);
        Assert.Equal(p.Name, repo.Load(file).Name);
        string auto = repo.Autosave(p);
        Assert.Equal(auto, repo.LatestAutosave());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Reports_CsvAndExcelAreWritten()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Infeed.PackageCount = 3;
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        var r = e.ComputeResults();
        string dir = Path.Combine(Path.GetTempPath(), "ils_rep_" + Guid.NewGuid().ToString("N"));
        var files = ReportExporter.ExportCsv(r, e, dir);
        Assert.Equal(5, files.Count);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length > 20));
        Assert.Contains("Tempo ciclo medio", File.ReadAllText(files[0]));
        string xlsx = Path.Combine(dir, "report.xlsx");
        ReportExporter.ExportExcel(r, e, xlsx);
        Assert.True(new FileInfo(xlsx).Length > 1000);
        Directory.Delete(dir, true);
    }
}

public class ValidatorTests
{
    [Fact]
    public void InsufficientBrakingSpace_IsError()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Photocells.First(x => x.Id == "FC1").PositionMm = 12900;
        var r = ProjectValidator.Validate(p);
        Assert.Contains(r.Errors, i => i.Code == "PC_BRAKE" && i.ComponentId == "FC1");
    }

    [Fact]
    public void ReverseSection_IsReportedAsUnsupported()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Sections[0].Direction = TransportDirection.Reverse;
        Assert.Contains(ProjectValidator.Validate(p).Errors, i => i.Code == "SEC_DIR");
    }

    [Fact]
    public void MoreThanThreeStrappers_IsError()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Strappers.Add(new StrapperConfig { Id = "L4", PositionMm = 28000 });
        Assert.Contains(ProjectValidator.Validate(p).Errors, i => i.Code == "STR_COUNT");
    }

    [Fact]
    public void OverlappingStrapperFootprints_IsError()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Strappers[1].PositionMm = p.Strappers[0].PositionMm + 500;
        Assert.Contains(ProjectValidator.Validate(p).Errors, i => i.Code == "STR_INTERF");
    }

    [Fact]
    public void GapBetweenSections_IsError()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Sections[1].StartMm += 100;
        Assert.Contains(ProjectValidator.Validate(p).Errors, i => i.Code == "SEC_GAP");
    }

    [Fact]
    public void StrapOutsidePackage_IsError()
    {
        var p = LegacySamples.CreateStandardLine();
        p.PackageTypes[0].StrapPositionsMm.Add(11900);
        Assert.Contains(ProjectValidator.Validate(p).Errors, i => i.Code == "STRAP_POS");
    }
}
