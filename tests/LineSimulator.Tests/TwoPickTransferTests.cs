using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// Trasferitore sollevabile a due prese (bilico a metà corsa) e trasferitore d'accumulo; valori del double banking senza fermo
/// (decisioni dell'utente del 2026-11-08).
/// </summary>
public class TwoPickTransferTests
{
    public static IEnumerable<object[]> Models() => SampleProjects.All.Select(m => new object[] { m.Name });
    private static LineProject Model(string name) => SampleProjects.All.Single(m => m.Name == name).Factory();

    private static SimulationEngine Run(LineProject p)
    {
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        var first = e.Events.FirstOrDefault(x => x.Type == SimEventType.TransferInterference);
        if (first != null)
            Assert.Fail("Interferenza:\n" + string.Join("\n", e.Events.Where(x => x.TimeS >= first.TimeS - 25 && x.TimeS <= first.TimeS + 0.5 && x.Severity >= EventSeverity.Info).Select(x => x.ToString())));
        Assert.True(e.CompletedNormally, e.FinishReason);
        return e;
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void AllPackagesAreWeighed_ThenGoOnTheAccumulationTransfer(string name)
    {
        var p = Model(name);
        Assert.True(p.Transfer.ScaleAtMidStroke);
        Assert.True(p.AccumulationTransfer.IsEnabled);
        var e = Run(p);
        Assert.True(e.Transfer.TwoPick);
        var coll = e.Events.Where(x => x.Type == SimEventType.Collision || x.Severity >= EventSeverity.Warning).Select(x => x.ToString()).Take(12).ToList();
        Assert.True(coll.Count == 0, string.Join("\n", coll));
        Assert.NotNull(e.Accumulation);
        Assert.Equal(p.Infeed.PackageCount, e.Accumulation!.Received);
        Assert.All(e.Packages, k => Assert.NotNull(k.MeasuredWeightKg));
        Assert.All(e.Packages, k => Assert.True(k.WeighedTimeS <= k.ExitTimeS + 1e-9, $"{k.Id} uscito prima di essere pesato"));
    }

    [Fact]
    public void ScaleIsAtMidStroke_AndOneCycleCarriesTwoPackages()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var e = Run(p);
        double half = p.Transfer.TranslationStrokeMm / 1000.0 / 2;
        // sul bilico il pacco sta a metà corsa
        var onScale = e.Recorder.Frames.SelectMany(f => f.Packages).Where(k => k.State == PackageState.OnScale).ToList();
        Assert.NotEmpty(onScale);
        Assert.All(onScale, k => Assert.Equal(half, k.LateralM, 6));
        // almeno un ciclo con due pacchi insieme: uno dalla via rulli verso il bilico, l'altro dal bilico verso l'accumulo
        Assert.Contains(e.Recorder.Frames, f =>
        {
            var on = f.Packages.Where(k => k.State == PackageState.OnTransfer).ToList();
            return on.Count == 2 && on.Any(k => k.LateralM < half - 1e-6) && on.Any(k => k.LateralM > half + 1e-6);
        });
        // ogni traslazione è di mezza corsa
        Assert.All(e.Recorder.Frames, f => Assert.True(f.TransferLateralM <= half + 1e-6));
    }

    [Fact]
    public void AccumulationTransfer_StepsByPackageWidthPlusGap_AndUnloadsAtTheEnd()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Infeed.PackageCount = 12;
        var e = Run(p);
        var acc = e.Accumulation!;
        double step = p.PackageTypes[0].GraphicWidthMm / 1000.0 + p.AccumulationTransfer.StepGapMm / 1000.0;
        Assert.All(p.PackageTypes, t => Assert.Equal(p.PackageTypes[0].GraphicWidthMm, t.GraphicWidthMm));
        // posizioni: multipli del passo, una per pacco, tutte dentro la larghezza utile
        var offsets = acc.Items.Select(i => i.OffsetM).OrderBy(x => x).ToList();
        for (int i = 0; i < offsets.Count; i++) Assert.Equal(step * (i + 1), offsets[i], 3);
        Assert.All(acc.Items, i => Assert.True(i.OffsetM + i.WidthM <= acc.WidthM + 1e-6));
        Assert.True(acc.Unloaded > 0);
        Assert.Equal(12, acc.Received);
        Assert.Equal(acc.Received, acc.Items.Count + acc.Unloaded);
        // nella vista 3D i pacchi accumulati compaiono oltre la corsa del trasferitore
        double stroke = p.Transfer.TranslationStrokeMm / 1000.0;
        var last = e.CaptureFrame();
        Assert.All(last.Packages.Where(k => k.State == PackageState.OnAccumulation), k => Assert.True(k.LateralM > stroke));
    }

    [Fact]
    public void WithoutTheOption_TheTransferMakesTheFullStroke()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Transfer.ScaleAtMidStroke = false;
        var e = Run(p);
        Assert.False(e.Transfer.TwoPick);
        Assert.Null(e.Accumulation);
        Assert.Contains(e.Recorder.Frames, f => f.TransferLateralM > p.Transfer.TranslationStrokeMm / 1000.0 - 1e-6);
        Assert.All(e.Packages, k => Assert.NotNull(k.MeasuredWeightKg));
    }

    [Fact]
    public void DoubleBanking_DefaultGap500_OverhangNeverBeyond2m()
    {
        var t = new TransferConfig();
        Assert.Equal(500, t.DoubleBankingGapMm);
        t.MaxOverhangMm = 3000;
        Assert.Equal(2000, t.EffectiveMaxOverhangMm);
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Transfer.MaxOverhangMm = 2500;
        Assert.Contains(ProjectValidator.Validate(p).Warnings, w => w.Code == "TRF_OVERHANG_MAX");
        p.Transfer.MaxOverhangMm = 2000;
        Assert.DoesNotContain(ProjectValidator.Validate(p).Warnings, w => w.Code == "TRF_OVERHANG_MAX");
    }

    /// <summary>Confronto di produttività (solo per le anteprime della CI): due prese / corsa intera e configurazioni dello svincolo.</summary>
    [Fact]
    public void ProductivityReport_ForThePreviews()
    {
        string? dir = Environment.GetEnvironmentVariable("PDF_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var lines = new List<string> { "Modello;t/h corsa intera;t/h due prese (esempio);t/h svincolo motori commutabili;t/h parcheggio svincolo;partenze a legatura finita esempio/commutabili/parcheggio" };
        foreach (var (name, create) in SampleProjects.All)
        {
            (double Tph, int Together) Run(Func<LineProject, bool> change)
            {
                var p = create();
                if (!change(p)) return (double.NaN, 0);
                var e = new SimulationEngine(p);
                e.RunToCompletion();
                if (!e.CompletedNormally || e.Collisions > 0) return (double.NaN, 0);
                var exits = e.Packages.Where(k => k.ExitTimeS.HasValue).ToList();
                double span = exits.Max(k => k.ExitTimeS!.Value) - exits.Min(k => k.EntryTimeS);
                int together = e.Events.Count(x => x.Message.Contains("liberato la fotocellula di uscita legatura"));
                return (span > 0 ? exits.Sum(k => k.WeightKg) / 1000.0 / (span / 3600.0) : 0, together);
            }
            var full = Run(p => { p.Transfer.ScaleAtMidStroke = false; return true; });
            var two = Run(_ => true);
            var sw = Run(p =>
            {
                var m = StrappingAreaRules.ReleaseModules(p).LastOrDefault();
                if (m == null) return false;
                m.CommandZoneId = string.Empty; m.FollowsStrapping = true; return true;
            });
            var park = Run(p => SimultaneousStartTests.ApplyParking(p));
            string F(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0");
            lines.Add($"{name};{F(full.Tph)};{F(two.Tph)};{F(sw.Tph)};{F(park.Tph)};{two.Together}/{sw.Together}/{park.Together}");
        }
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "confronto-due-prese.csv"), lines);
    }
}
