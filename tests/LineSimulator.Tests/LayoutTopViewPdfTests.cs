using System.Text;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>PDF della vista dall'alto: sezioni inverter, motori commutabili, quote tra fotocellule, fermi e legatrici.</summary>
public class LayoutTopViewPdfTests
{
    public static IEnumerable<object[]> Models() => SampleProjects.All.Select(m => new object[] { m.Name });
    private static LineProject Model(string name) => SampleProjects.All.Single(m => m.Name == name).Factory();

    /// <summary>Se impostata, i PDF di prova vengono salvati qui (anteprime pubblicate dalla CI).</summary>
    private static string? PreviewDir => Environment.GetEnvironmentVariable("PDF_PREVIEW_DIR");

    private static void Preview(string file, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(PreviewDir)) return;
        Directory.CreateDirectory(PreviewDir);
        File.WriteAllBytes(Path.Combine(PreviewDir, file), bytes);
    }

    private static string Slug(string s) => new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');

    [Theory]
    [MemberData(nameof(Models))]
    public void TopView_IsWellFormed_AndListsEveryElement(string name)
    {
        var p = Model(name);
        string before = ProjectSerializer.Serialize(p);
        var bytes = LayoutTopViewPdf.Build(p);
        Preview("vista-dall-alto_" + Slug(name) + ".pdf", bytes);
        Assert.Equal(before, ProjectSerializer.Serialize(p));                 // il progetto aperto non cambia

        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("vista dall'alto", text);
        Assert.Contains("Sezioni inverter", text);
        Assert.Contains("Fotocellule tra loro", text);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(text, "/Type /Page /Parent").Count >= 2);
        // i nomi sono quelli della simulazione (stesse correzioni automatiche dell'avvio)
        var q = ProjectSerializer.Clone(p);
        PhotocellRules.BeforeSimulation(q);
        SimulationPreparation.Prepare(q);
        foreach (var pc in q.Photocells.Where(x => x.IsEnabled))
            Assert.Contains("(" + pc.Id + ")", text);
        foreach (var s in q.Strappers.Where(x => x.IsEnabled))
            Assert.Contains("(" + s.Id + " ", text);
    }

    [Fact]
    public void Zones_GroupModulesOfTheSameInverter()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        PhotocellRules.BeforeSimulation(p);
        SimulationPreparation.Prepare(p);
        var zones = LayoutTopViewPdf.Zones(p);
        Assert.Equal(p.Sections.Select(s => s.EffectiveZoneId).Distinct().Count(), zones.Count);
        Assert.Equal(p.Sections.Count, zones.Sum(z => z.Sections.Count));
        Assert.All(zones, z => Assert.True(z.EndMm > z.StartMm));
    }

    [Fact]
    public void SwitchableMotors_AreMarked()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var release = StrappingAreaRules.ReleaseModules(p).FirstOrDefault();
        Assert.NotNull(release);
        release!.CommandZoneId = string.Empty;
        release.FollowsStrapping = true;
        var text = Encoding.Latin1.GetString(LayoutTopViewPdf.Build(p));
        Assert.Contains("motori commutabili", text);
        Preview("vista-dall-alto_motori-commutabili.pdf", LayoutTopViewPdf.Build(p));
    }

    [Fact]
    public void GanttPreview_ForTheDefaultModel()
    {
        if (string.IsNullOrWhiteSpace(PreviewDir)) return;
        var e = new SimulationEngine(SampleProjects.CreateTwoStrappers12m());
        e.RunToCompletion();
        Preview("ciclogramma-gantt_2-legatrici.pdf", PackageGanttPdf.Build(PackageGantt.Build(e.Recorder), StepCyclogram.Build(e.Recorder), "2 legatrici - pacchi 6/9/12 m"));
    }
}
