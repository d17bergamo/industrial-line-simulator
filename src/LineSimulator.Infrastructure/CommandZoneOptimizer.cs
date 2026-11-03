using System.Globalization;
using System.Text;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Models;

namespace LineSimulator.Infrastructure;

/// <summary>Configurazione candidata: suddivisione dei moduli (in ordine lungo la linea) in zone di comando contigue.</summary>
public sealed class ZonePartitionCandidate
{
    public required IReadOnlyList<IReadOnlyList<string>> Groups { get; init; }
    public int Inverters => Groups.Count;
    public bool Valid { get; init; }
    public string Reason { get; init; } = string.Empty;
    /// <summary>Produttività effettiva (pacchi/h) per tipologia; la media pesa tutte le configurazioni di pacco.</summary>
    public double? PackagesPerHour { get; init; }
    public double? CycleTimeS { get; init; }
    public string Label => string.Join(" | ", Groups.Select(g => string.Join("+", g)));
}

public sealed class CommandZoneOptimizationReport
{
    public List<ZonePartitionCandidate> Candidates { get; } = new();
    public ZonePartitionCandidate? Best { get; set; }
    public int EvaluatedCandidates => Candidates.Count;
    public bool Truncated { get; set; }
    public string Explanation { get; set; } = string.Empty;
}

/// <summary>
/// Ottimizzazione delle zone di comando (§10 "Obiettivo di ottimizzazione"): le lunghezze dei moduli, le macchine,
/// i trasferitori e i bilici sono fissi; si sceglie quanti inverter usare raggruppando moduli contigui.
/// Ogni candidata viene simulata per tutte le tipologie di pacco; è ammessa solo se ogni simulazione termina
/// senza collisioni, blocchi, invasioni di zona, errori di posizionamento o pacchi non legati.
/// Criterio: minimo numero di inverter; a parità, produttività maggiore (media sulle tipologie).
/// </summary>
public static class CommandZoneOptimizer
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <param name="packagesPerType">Pacchi simulati per ogni tipologia (prova breve ma a regime).</param>
    /// <param name="maxCandidates">Numero massimo di suddivisioni simulate (2^(n-1) per n moduli).</param>
    public static CommandZoneOptimizationReport Optimize(LineProject source, int packagesPerType = 4, int maxCandidates = 128,
        IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var report = new CommandZoneOptimizationReport();
        var modules = source.Sections.OrderBy(s => s.StartMm).Select(s => s.Id).ToList();
        int n = modules.Count;
        if (n == 0) { report.Explanation = "Nessun modulo di via a rulli."; return report; }

        // Candidate ordinate per numero di inverter crescente: la prima ammessa con meno inverter è già il vincitore di classe.
        var all = new List<List<List<string>>>();
        long total = 1L << Math.Min(30, n - 1);
        for (long mask = 0; mask < total; mask++)
        {
            var groups = new List<List<string>> { new() { modules[0] } };
            for (int i = 1; i < n; i++)
            {
                if ((mask & (1L << (i - 1))) != 0) groups.Add(new List<string>());
                groups[^1].Add(modules[i]);
            }
            all.Add(groups);
        }
        var ordered = all.OrderBy(g => g.Count).ThenBy(g => string.Join("|", g.Select(x => string.Join("+", x))), StringComparer.Ordinal).ToList();
        if (ordered.Count > maxCandidates)
        {
            report.Truncated = true;
            // Si tengono sempre la configurazione attuale (un inverter per modulo) e le candidate con meno inverter.
            ordered = ordered.Take(maxCandidates - 1).Append(ordered.Last()).ToList();
        }

        int? bestInverters = null;
        foreach (var groups in ordered)
        {
            cancel.ThrowIfCancellationRequested();
            if (bestInverters.HasValue && groups.Count > bestInverters.Value) break; // nessun miglioramento possibile
            progress?.Report($"Simulazione {report.Candidates.Count + 1}: {groups.Count} inverter ({string.Join(" | ", groups.Select(g => string.Join("+", g)))})");
            var cand = Evaluate(source, groups, packagesPerType);
            report.Candidates.Add(cand);
            if (cand.Valid && (bestInverters == null || groups.Count <= bestInverters.Value))
            {
                if (report.Best == null || cand.Inverters < report.Best.Inverters
                    || (cand.Inverters == report.Best.Inverters && (cand.PackagesPerHour ?? 0) > (report.Best.PackagesPerHour ?? 0) + 1e-9))
                    report.Best = cand;
                bestInverters = report.Best.Inverters;
            }
        }
        report.Explanation = Explain(report, n);
        return report;
    }

    /// <summary>Applica la suddivisione al progetto: i moduli di ogni gruppo prendono come zona di comando l'ID del primo modulo.</summary>
    public static void Apply(LineProject project, ZonePartitionCandidate candidate)
    {
        foreach (var g in candidate.Groups)
            foreach (var id in g)
            {
                var s = project.Sections.FirstOrDefault(x => x.Id == id);
                if (s != null) s.CommandZoneId = g.Count > 1 ? g[0] : string.Empty;
            }
    }

    private static ZonePartitionCandidate Evaluate(LineProject source, List<List<string>> groups, int packagesPerType)
    {
        var p = ProjectSerializer.Clone(source);
        var cand = new ZonePartitionCandidate { Groups = groups };
        Apply(p, cand);
        var val = ProjectValidator.Validate(p);
        if (val.HasErrors)
            return new ZonePartitionCandidate { Groups = groups, Valid = false, Reason = "configurazione non valida: " + val.Errors.First().Message };

        var pph = new List<double>();
        var cycles = new List<double>();
        var types = p.PackageTypes.Count > 0 ? p.PackageTypes : new List<PackageType>();
        foreach (var t in types)
        {
            var q = ProjectSerializer.Clone(p);
            q.Infeed.TypeSequence = new List<string> { t.Id };
            q.Infeed.PackageCount = Math.Max(2, packagesPerType);
            q.Rejector.IsEnabled = false;
            var e = new SimulationEngine(q);
            e.RunToCompletion();
            string? why = !e.CompletedNormally ? e.FinishReason
                : e.Collisions > 0 ? $"{e.Collisions} collisioni"
                : e.ZoneOverfills > 0 ? "pacco entrato in una zona occupata"
                : e.PositioningErrors > 0 ? "errori di posizionamento"
                : e.Packages.Any(k => k.StrapStatus is LineSimulator.Engine.Runtime.StrapStatus.Failed or LineSimulator.Engine.Runtime.StrapStatus.Pending && k.StrapsTotal > 0) ? "pacchi non legati"
                : null;
            if (why != null)
                return new ZonePartitionCandidate { Groups = groups, Valid = false, Reason = $"tipologia {t.Id}: {why}" };
            var r = e.ComputeResults();
            double? v = r.PackagesPerHour ?? r.OverallPackagesPerHour;
            if (v.HasValue) pph.Add(v.Value);
            if (r.CycleTimeMeanS.HasValue) cycles.Add(r.CycleTimeMeanS.Value);
        }
        return new ZonePartitionCandidate
        {
            Groups = groups, Valid = true, Reason = "ammessa",
            PackagesPerHour = pph.Count > 0 ? pph.Average() : null,
            CycleTimeS = cycles.Count > 0 ? cycles.Average() : null
        };
    }

    private static string Explain(CommandZoneOptimizationReport r, int modules)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Moduli: {modules}. Suddivisioni simulate: {r.EvaluatedCandidates}{(r.Truncated ? " (elenco limitato)" : "")}.");
        if (r.Best == null)
        {
            sb.AppendLine("Nessuna suddivisione ammessa: verificare fotocellule di accumulo e di ripartenza.");
            return sb.ToString();
        }
        sb.AppendLine($"Migliore: {r.Best.Inverters} inverter [{r.Best.Label}], produttività media {r.Best.PackagesPerHour?.ToString("0.0", Ci) ?? "-"} pacchi/h" +
                      (r.Best.CycleTimeS.HasValue ? $", tempo ciclo medio {r.Best.CycleTimeS.Value.ToString("0.0", Ci)} s." : "."));
        var current = r.Candidates.FirstOrDefault(c => c.Inverters == modules);
        if (current != null && current != r.Best)
            sb.AppendLine($"Configurazione con un inverter per modulo ({modules} inverter): {(current.Valid ? $"{current.PackagesPerHour?.ToString("0.0", Ci)} pacchi/h" : "non ammessa (" + current.Reason + ")")}.");
        foreach (var c in r.Candidates.Where(c => !c.Valid).Take(5))
            sb.AppendLine($"  • scartata [{c.Label}]: {c.Reason}");
        sb.Append("Criterio: minimo numero di inverter; a parità, produttività maggiore su tutte le tipologie di pacco.");
        return sb.ToString();
    }
}
