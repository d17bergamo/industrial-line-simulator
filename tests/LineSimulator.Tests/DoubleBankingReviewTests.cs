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
        sb.AppendLine("Modello;Configurazione;Esito;t/h;Collisioni;Avvisi;Cicli trasferitore;Pacchi");
        var details = new StringBuilder();
        foreach (var (name, create) in SampleProjects.All)
        {
            foreach (var withStop in new[] { true, false })
            {
                var p = create();
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
                string cfg = withStop ? "double banking con fermo scomparente" : "double banking senza fermo (posizione virtuale)";
                var e = new SimulationEngine(p);
                e.RunToCompletion();
                var warn = e.Events.Where(x => x.Severity >= EventSeverity.Warning).ToList();
                var exits = e.Packages.Where(k => k.ExitTimeS.HasValue).ToList();
                double span = exits.Count > 0 ? exits.Max(k => k.ExitTimeS!.Value) - exits.Min(k => k.EntryTimeS) : 0;
                double tph = span > 0 ? exits.Sum(k => k.WeightKg) / 1000.0 / (span / 3600.0) : 0;
                string outcome = !e.CompletedNormally ? "NON COMPLETATA: " + e.FinishReason : e.Collisions > 0 ? "collisioni" : warn.Count > 0 ? "avvisi" : "pulita";
                sb.AppendLine($"{name};{cfg};{outcome};{tph:0.0};{e.Collisions};{warn.Count};{e.Transfer.Cycles};{exits.Count}/{e.Packages.Count}");
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
}
