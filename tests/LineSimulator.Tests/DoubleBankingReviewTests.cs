using System.Text;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// Riverifica del double banking (decisione dell'utente del 2026-11-13): modelli con e senza fermo scomparente.
/// Per le anteprime della CI scrive un rapporto con t/h, collisioni e gli eventi attorno alla prima anomalia.
/// </summary>
public class DoubleBankingReviewTests
{
    [Fact]
    public void Report_ForThePreviews()
    {
        string? dir = Environment.GetEnvironmentVariable("PDF_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var sb = new StringBuilder();
        sb.AppendLine("Modello;Configurazione;Esito;t/h;Collisioni;Avvisi;Cicli trasferitore;Cicli con 2 pacchi;Pacchi;Strisciamenti;Divisione via trasferitore [mm]");
        var details = new StringBuilder();
        foreach (var (name, create) in SampleProjects.All)
        {
            foreach (var (withStop, close) in new[] { (true, false), (false, false), (true, true), (false, true) })
            {
                var p = create();
                if (close)
                {
                    // trasferitore collo di bottiglia: pesatura lunga, i pacchi legati si accumulano davanti al trasferitore
                    foreach (var b in p.Scales) { b.StabilizationTimeS = 150; b.ConfirmTimeoutS = 400; }
                }
                bool hadStop = p.IntermediateStops.Any(s => s.IsEnabled);
                if (withStop && !hadStop)
                {
                    p.IntermediateStops.Add(new IntermediateStopConfig
                    {
                        Id = "FM1", Name = "Fermo scomparente", Placement = DevicePlacement.Center,
                        PositionMm = StrappingAreaRules.StopPositionMm(p, DevicePlacement.Center), AlignmentTimeS = 2.0
                    });
                }
                if (!withStop) p.IntermediateStops.Clear();
                BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
                string cfg = (withStop ? "double banking con fermo scomparente" : "double banking senza fermo (posizione virtuale)") + (close ? ", pesatura lunga (150 s): trasferitore collo di bottiglia" : "");
                var e = new SimulationEngine(p);
                e.RunToCompletion();
                var warn = e.Events.Where(x => x.Severity >= EventSeverity.Warning).ToList();
                // cicli con due pacchi dalla via rulli: "Trasferitore: sollevamento (P001+P002)"
                int pairs = e.Events.Count(x => x.Type == SimEventType.TransferPhase && x.Message.StartsWith("Trasferitore: sollevamento ("));
                var exits = e.Packages.Where(k => k.ExitTimeS.HasValue).ToList();
                double span = exits.Count > 0 ? exits.Max(k => k.ExitTimeS!.Value) - exits.Min(k => k.EntryTimeS) : 0;
                double tph = span > 0 ? exits.Sum(k => k.WeightKg) / 1000.0 / (span / 3600.0) : 0;
                string outcome = !e.CompletedNormally ? "NON COMPLETATA: " + e.FinishReason : e.Collisions > 0 ? "collisioni" : warn.Count > 0 ? "avvisi" : "pulita";
                sb.AppendLine($"{name};{cfg};{outcome};{tph:0.0};{e.Collisions};{warn.Count};{e.Transfer.Cycles};{pairs};{exits.Count}/{e.Packages.Count};{e.SlipEvents};{(e.TransferSplitM is double sp ? (sp * 1000).ToString("0") : "-")}");
                if (warn.Count > 0 || !e.CompletedNormally)
                {
                    var first = warn.FirstOrDefault();
                    double t0 = first?.TimeS ?? e.Events.LastOrDefault()?.TimeS ?? 0;
                    details.AppendLine($"=== {name} — {cfg} — {outcome}");
                    foreach (var w in warn.Take(8)) details.AppendLine("  ! " + w);
                    details.AppendLine("  eventi prima della prima anomalia:");
                    foreach (var x in e.Events.Where(x => x.TimeS >= t0 - 20 && x.TimeS <= t0 + 0.5).TakeLast(40)) details.AppendLine("    " + x);
                    details.AppendLine();
                }
            }
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "double-banking.csv"), sb.ToString());
        File.WriteAllText(Path.Combine(dir, "double-banking-dettagli.txt"), details.Length > 0 ? details.ToString() : "Nessuna anomalia.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoubleBanking_WhenTheTransferIsTheBottleneck_PairsWithTheStop_RunsCleanWithout(bool withStop)
    {
        var p = SampleProjects.All.Single(m => m.Name.StartsWith("3 legatrici")).Factory();
        if (!withStop) p.IntermediateStops.Clear();
        foreach (var b in p.Scales) { b.StabilizationTimeS = 150; b.ConfirmTimeoutS = 400; }
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        string Dump() => string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Info && x.TimeS <= (e.Events.LastOrDefault()?.TimeS ?? 0))
                                                    .TakeLast(45).Select(x => x.ToString()));
        Assert.True(e.CompletedNormally, e.FinishReason + "\n" + Dump());
        Assert.Equal(AccumulationMode.DoubleBanking, e.EffectiveBanking);
        var bad = e.Events.Where(x => x.Severity >= EventSeverity.Warning).Select(x => x.ToString()).Take(10).ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.Equal(0, e.Collisions);
        // senza fermo il secondo pacco finisce in parte sul bilico del primo: dal 2026-11-18 si abbinano comunque, con la
        // pesata in due tempi del trasferitore a doppio comando (il secondo peso è per differenza)
        Assert.True(e.Events.Any(x => x.Type == SimEventType.TransferPhase && x.Message.StartsWith("Trasferitore: sollevamento (")),
            "nessun ciclo con due pacchi\n" + string.Join("\n", e.Events.Where(x => x.Type is SimEventType.TransferPhase or SimEventType.TransferWaiting or SimEventType.InfeedWaiting || x.Message.Contains("fermo") || x.Message.Contains("POS")).Take(45).Select(x => x.ToString())));
        Assert.True(e.Transfer.Cycles < e.Packages.Count, $"{e.Transfer.Cycles} cicli per {e.Packages.Count} pacchi");
    }

    /// <summary>
    /// Revisione 2026-11-14: il secondo pacco in double banking non deve slittare sui rulli. Prima la via del trasferitore
    /// restava in velocità bassa (fotocellula di rallentamento occupata dal primo pacco già in battuta sul fermo finale) e il
    /// secondo pacco ci entrava a velocità alta: 0,69 m/s di differenza. Il rallentamento ora vale solo per il pacco in arrivo.
    /// </summary>
    [Theory]
    [InlineData("3 legatrici")]
    [InlineData("2 legatrici - pacchi 6/9/12/18 m")]
    [InlineData("2 legatrici - pacchi 6/9/12 m")]
    [InlineData("1 legatrice")]
    public void DoubleBanking_SecondPackage_NeverSlipsOnTheRollers(string model)
    {
        var p = SampleProjects.All.Single(m => m.Name.StartsWith(model)).Factory();
        foreach (var b in p.Scales) { b.StabilizationTimeS = 150; b.ConfirmTimeoutS = 400; }
        if (!p.IntermediateStops.Any(s => s.IsEnabled))
            p.IntermediateStops.Add(new IntermediateStopConfig
            {
                Id = "FM1", Name = "Fermo scomparente", Placement = DevicePlacement.Center,
                PositionMm = StrappingAreaRules.StopPositionMm(p, DevicePlacement.Center), AlignmentTimeS = 2.0
            });
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        var slips = e.Events.Where(x => x.Type == SimEventType.Slip).Select(x => x.ToString()).Take(10).ToList();
        Assert.True(e.SlipEvents == 0, string.Join("\n", slips));
        Assert.Equal(0, e.Collisions);
    }

    /// <summary>
    /// Revisione 2026-11-14: con i motori dello svincolo commutati sull'inverter di legatura il registro eventi riceveva
    /// lo stesso comando dello svincolo a ogni scansione (10 ms). Un comando va nel registro solo quando cambia.
    /// </summary>
    [Fact]
    public void SwitchedReleaseMotors_DoNotFloodTheEventLog()
    {
        var p = SampleProjects.All.Single(m => m.Name.StartsWith("3 legatrici")).Factory();
        Assert.Contains(p.Sections, s => s.FollowsStrapping);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Contains(e.Events, x => x.Message.Contains("motori commutati sull'inverter di legatura", StringComparison.Ordinal));
        foreach (var g in e.Events.Where(x => x.Type == SimEventType.InverterCommand && x.ComponentId != null).GroupBy(x => x.ComponentId))
        {
            var list = g.ToList();
            for (int i = 1; i < list.Count; i++)
                Assert.False(list[i].Message == list[i - 1].Message && list[i].TimeS - list[i - 1].TimeS < 2.5 * e.Dt,
                    $"comando ripetuto a ogni scansione: {list[i]}");
        }
    }
}
