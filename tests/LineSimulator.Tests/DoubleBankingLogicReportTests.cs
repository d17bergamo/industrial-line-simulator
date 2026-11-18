using System.Globalization;
using System.Text;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// Rapporto sulla logica del double banking nei quattro esempi, con intestatura di testa e di coda, con e senza fermo
/// scomparente: geometria, tipologie che si abbinano, cicli del motore (per le anteprime della CI, 2026-11-17).
/// Non verifica nulla: serve a descrivere ciò che il motore fa davvero.
/// </summary>
public class DoubleBankingLogicReportTests
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static string M(double mm) => mm.ToString("0", Ci);

    [Fact]
    public void Report_ForThePreviews()
    {
        string? dir = Environment.GetEnvironmentVariable("PDF_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var sb = new StringBuilder();
        foreach (var (name, create) in SampleProjects.All)
        {
            foreach (bool withStop in new[] { true, false })
            foreach (var heading in new[] { StopReference.Head, StopReference.Tail })
            {
                var p = Setup(create(), withStop, heading);
                sb.AppendLine($"##### {name} | {(withStop ? (heading == StopReference.Tail ? "con fermo scomparente all'estremità a monte" : "con fermo scomparente al centro") : "senza fermo scomparente")} | intestatura di {(heading == StopReference.Head ? "TESTA" : "CODA")}");
                var probe = ProjectSerializer.Clone(p);
                var prep = SimulationPreparation.Prepare(probe);
                var tr = probe.Transfer;
                sb.AppendLine($"Trasferitore {M(tr.PositionMm)}..{M(tr.PositionMm + tr.LengthMm)} mm; fermo finale a {M(probe.Stopper.PositionMm)} mm; " +
                              $"fermi scomparenti: {(probe.IntermediateStops.Any(s => s.IsEnabled) ? string.Join(", ", probe.IntermediateStops.Where(s => s.IsEnabled).Select(s => $"{s.Id} a {M(s.PositionMm)} mm (extra corsa {M(s.TailOvershootMm)})")) : "nessuno")}; " +
                              $"bilici: {string.Join(", ", probe.Scales.Select(b => $"{b.Id} {M(b.StartMm)}..{M(b.StartMm + b.LengthMm)}"))}");
                double split = TransferSplitRule.SplitMm(probe);
                sb.AppendLine($"Divisione della via in due inverter: {(split > 0 ? M(split) + " mm" : "nessuna")}");
                foreach (var c in prep.Changes) sb.AppendLine("  preparazione: " + c);
                foreach (var t in probe.PackageTypes)
                {
                    var b = BankingOptions.Banking(probe, t);
                    sb.Append($"  {t.Id}: banking {(b == AccumulationMode.DoubleBanking ? "DOUBLE" : "single")}, intestatura {(BankingOptions.Heading(probe, t) == StopReference.Head ? "testa" : "coda")}");
                    if (BankingOptions.DoubleBankingBlockReason(probe, t) is { } why) sb.Append($" (double non possibile: {why})");
                    sb.AppendLine();
                }

                var shortest = p.PackageTypes.OrderBy(t => t.LengthMm).First();
                var all = p.Infeed.TypeSequence.ToList();
                sb.AppendLine("  -- Prova A: solo pacchi da " + M(shortest.LengthMm) + " mm, pesatura lunga (150 s): il trasferitore è il collo di bottiglia");
                Run(sb, p, Enumerable.Repeat(shortest.Id, 6).ToList(), longWeigh: true, detail: true);
                sb.AppendLine("  -- Prova B: sequenza dell'esempio, pesatura lunga (150 s)");
                Run(sb, p, all.Concat(all).ToList(), longWeigh: true, detail: false);
                sb.AppendLine("  -- Prova C: sequenza dell'esempio, pesatura normale");
                Run(sb, p, all.Concat(all).ToList(), longWeigh: false, detail: false);
                sb.AppendLine();
            }
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "double-banking-logica.txt"), sb.ToString());
    }

    private static LineProject Setup(LineProject p, bool withStop, StopReference heading)
    {
        // con l'intestatura di coda il secondo pacco sta oltre il fermo: il fermo va all'estremità a monte del trasferitore
        var place = heading == StopReference.Tail ? DevicePlacement.End : DevicePlacement.Center;
        if (withStop)
        {
            p.IntermediateStops.Clear();
            p.IntermediateStops.Add(new IntermediateStopConfig
            {
                Id = "FM1", Name = "Fermo scomparente", Placement = place,
                PositionMm = StrappingAreaRules.StopPositionMm(p, place), AlignmentTimeS = 2.0
            });
        }
        if (!withStop) p.IntermediateStops.Clear();
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        p.Simulation.Heading = heading;
        foreach (var t in p.PackageTypes) t.Heading = heading;
        return p;
    }

    private static void Run(StringBuilder sb, LineProject source, List<string> sequence, bool longWeigh, bool detail)
    {
        var p = ProjectSerializer.Clone(source);
        p.Infeed.TypeSequence = sequence;
        p.Infeed.PackageCount = sequence.Count;
        if (longWeigh) foreach (var b in p.Scales) { b.StabilizationTimeS = 150; b.ConfirmTimeoutS = 400; }
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        var exits = e.Packages.Where(k => k.ExitTimeS.HasValue).ToList();
        double span = exits.Count > 0 ? exits.Max(k => k.ExitTimeS!.Value) - exits.Min(k => k.EntryTimeS) : 0;
        double tph = span > 0 ? exits.Sum(k => k.WeightKg) / 1000.0 / (span / 3600.0) : 0;
        var lifts = e.Events.Where(x => x.Type == SimEventType.TransferPhase && x.Message.StartsWith("Trasferitore: sollevamento (", StringComparison.Ordinal)).ToList();
        string legend = string.Join(" ", e.Packages.Select(k => $"{k.Id}={k.LengthM * 1000:0}"));
        sb.AppendLine($"     esito: {(e.CompletedNormally ? "completata" : "NON completata: " + e.FinishReason)}; {tph:0.0} t/h; cicli del trasferitore {e.Transfer.Cycles} per {e.Packages.Count} pacchi; " +
                      $"cicli con 2 pacchi {lifts.Count}; collisioni {e.Collisions}; strisciamenti {e.SlipEvents}; avvisi {e.Events.Count(x => x.Severity >= EventSeverity.Warning)}");
        sb.AppendLine($"     pacchi (mm): {legend}");
        foreach (var l in lifts) sb.AppendLine($"     {l.TimeS:0.0} s  {l.Message}");
        foreach (var w in e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(4)) sb.AppendLine("     ! " + w);
        if (!detail) return;
        sb.AppendLine("     eventi del ciclo (fermi, trasferitore, attese):");
        var keep = e.Events.Where(x => x.Type is SimEventType.StopPhase or SimEventType.TransferWaiting
                                       || (x.Type == SimEventType.TransferPhase && (x.Message.Contains("sollevamento") || x.Message.Contains("deposito") || x.Message.Contains("metà")))
                                       || x.Type == SimEventType.WeightAcquired
                                       || (x.Type == SimEventType.AlignmentCompleted)).Take(60);
        foreach (var x in keep) sb.AppendLine("       " + x);
    }
}
