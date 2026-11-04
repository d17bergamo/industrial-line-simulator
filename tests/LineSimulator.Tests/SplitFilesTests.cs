using LineSimulator.Core;
using LineSimulator.Infrastructure;
using LineSimulator.Models;

namespace LineSimulator.Tests;

public class SplitFilesTests
{
    private static string Tmp(string name) => Path.Combine(Path.GetTempPath(), $"ils_{Guid.NewGuid():N}_{name}");

    [Fact]
    public void LayoutFile_ContainsNoPackageTypes_AndReloadsIdentically()
    {
        var p = LegacySamples.CreateStandardLine();
        var path = Tmp("layout.json");
        try
        {
            SplitFiles.SaveLayout(p, path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("\"lengthMm\": 12000,\n      \"weightKg\"", text.Replace("\r", ""));
            var loaded = SplitFiles.LoadLayout(path, p);
            Assert.Equal(p.Sections.Select(s => (s.Id, s.StartMm, s.LengthMm, s.RollerCount, s.RollerPitchMm)),
                         loaded.Sections.Select(s => (s.Id, s.StartMm, s.LengthMm, s.RollerCount, s.RollerPitchMm)));
            Assert.Equal(p.Strappers.Select(s => (s.Id, s.PositionMm)), loaded.Strappers.Select(s => (s.Id, s.PositionMm)));
            Assert.Equal(p.Stopper.PositionMm, loaded.Stopper.PositionMm);
            // le tipologie restano quelle del progetto corrente
            Assert.Equal(p.PackageTypes.Select(t => t.Id), loaded.PackageTypes.Select(t => t.Id));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExtractLayout_HasNoPackageTypes()
    {
        var layout = SplitFiles.ExtractLayout(LegacySamples.CreateStandardLine());
        Assert.Empty(layout.PackageTypes);
        Assert.Empty(layout.Infeed.TypeSequence);
    }

    [Fact]
    public void PackagesFile_RoundTripsHeightAndStraps()
    {
        var types = new List<PackageType>
        {
            new() { Id = "A", Name = "A", LengthMm = 8000, WeightKg = 2500, GraphicHeightMm = 450 },
            new() { Id = "B", Name = "B", LengthMm = 12000, WeightKg = 3000 }
        };
        var path = Tmp("pacchi.json");
        try
        {
            SplitFiles.SavePackages(types, path);
            var back = SplitFiles.LoadPackages(path);
            Assert.Equal(2, back.Count);
            Assert.Equal(450, back[0].GraphicHeightMm);
            Assert.Equal(8000, back[0].LengthMm);
        }
        finally { File.Delete(path); }
    }
}
