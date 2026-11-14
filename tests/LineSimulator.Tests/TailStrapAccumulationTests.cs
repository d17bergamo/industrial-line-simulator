using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// Regola dell'utente (2026-11-14): se durante l'ultima legatura di coda la testa del pacco entra in accumulo (oltre la
/// fotocellula di arresto davanti al trasferitore) e la produttività non è garantita, il software lo segnala e chiede se
/// allungare lo svincolo.
/// </summary>
public class TailStrapAccumulationTests
{
    /// <summary>Modello da 2 legatrici con in più un pacco da 18 m: la sua ultima legatura di coda porta la testa oltre lo svincolo.</summary>
    private static LineProject LongPackageCase()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var t = SampleProjects.Package(18);
        p.PackageTypes.Add(t);
        p.Infeed.TypeSequence = new List<string>(p.Infeed.TypeSequence) { t.Id };
        ProjectAutoFix.Run(p);
        return p;
    }

    private static string Info(LineProject p) =>
        $"testa {StrappingAreaRules.RequiredStrappingHeadMaxMm(p):0} mm, accumulo da {StrappingAreaRules.AccumulationEntryMm(p):0} mm, " +
        $"svincolo {string.Join(",", StrappingAreaRules.ReleaseModules(p).Select(m => $"{m.Id} {m.StartMm:0}-{m.EndMm:0}"))}";

    [Fact]
    public void HeadIntoAccumulation_IsSignalled_AndLengtheningTheReleaseRemovesIt()
    {
        var p = LongPackageCase();
        double over = StrappingAreaRules.HeadIntoAccumulationMm(p);
        Assert.True(over > 0, Info(p));
        Assert.Contains(EquipmentCheck.Run(p, fix: false), c => c.Code == "STR_HEAD_ACCUM" && !c.IsProblem);
        int n = StrappingAreaRules.ReleaseRollersForTailStrap(p);
        Assert.True(n > 0);
        Assert.True(StrappingAreaRules.LengthenReleaseForTailStrap(p));
        ProjectAutoFix.Run(p);
        Assert.True(StrappingAreaRules.HeadIntoAccumulationMm(p) <= 1e-6, Info(p));
        Assert.DoesNotContain(EquipmentCheck.Run(p, fix: false), c => c.Code == "STR_HEAD_ACCUM");
    }

    [Fact]
    public void Optimizer_WarnsAndProposesTheLongerRelease_WhenTheTargetIsNotReached()
    {
        var p = LongPackageCase();
        var r = ProductivityAdvisor.Analyze(p, 1000, maxSteps: 1);
        Assert.False(r.TargetReached);
        Assert.NotNull(r.ReleaseWarning);
        Assert.NotNull(r.ReleaseLengthening);
        Assert.Contains("entra in accumulo", r.Summary, StringComparison.Ordinal);
        Assert.True(r.ReleaseLengthening!.Feasible, r.ReleaseLengthening.Problem);
        Assert.NotNull(r.ReleaseProject);
        Assert.True(StrappingAreaRules.HeadIntoAccumulationMm(r.ReleaseProject!) <= 1e-6, Info(r.ReleaseProject!));

        // con lo svincolo allungato l'obiettivo minimo è garantito e non c'è più nessuna segnalazione
        var ok = ProductivityAdvisor.Analyze(r.ReleaseProject!, 1, maxSteps: 0);
        Assert.True(ok.TargetReached, ok.Summary);
        Assert.Null(ok.ReleaseWarning);
    }

    [Fact]
    public void Report_ForThePreviews()
    {
        string? dir = Environment.GetEnvironmentVariable("PDF_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var lines = new List<string> { "Modello;testa ultima legatura di coda (mm);inizio accumulo (mm);in accumulo (mm);rulli da aggiungere;t/h attuale;t/h svincolo allungato" };
        var cases = SampleProjects.All.Select(m => (m.Name, P: m.Factory())).Append(("2 legatrici 6-12 m + pacco 18 m (prova)", LongPackageCase()));
        foreach (var (name, p) in cases)
        {
            double over = StrappingAreaRules.HeadIntoAccumulationMm(p);
            int n = StrappingAreaRules.ReleaseRollersForTailStrap(p);
            string before = Tph(ProjectSerializer.Clone(p)), after = "-";
            var q = ProjectSerializer.Clone(p);
            if (StrappingAreaRules.LengthenReleaseForTailStrap(q)) { ProjectAutoFix.Run(q); after = Tph(q); }
            lines.Add($"{name};{StrappingAreaRules.RequiredStrappingHeadMaxMm(p):0};{StrappingAreaRules.AccumulationEntryMm(p):0};{over:0};{n};{before};{after}");
        }
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "legatura-coda-accumulo.csv"), lines);
    }

    private static string Tph(LineProject p)
    {
        p.Infeed.PackageCount = Math.Max(p.Infeed.PackageCount, ProductivityAdvisor.MeasurePackages);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        if (!e.CompletedNormally || e.Collisions > 0) return "non va: " + (e.CompletedNormally ? $"{e.Collisions} collisioni" : e.FinishReason);
        return (e.ComputeResults().TonnesPerHour ?? 0).ToString("0.0");
    }
}
