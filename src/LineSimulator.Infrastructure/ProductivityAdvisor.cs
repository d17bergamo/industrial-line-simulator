using System.Globalization;
using System.Text;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Infrastructure;

/// <summary>Una modifica proposta e il suo risultato simulato.</summary>
public sealed record AdvisorOption(string Id, string Title, string Description, double TonnesPerHour, double PackagesPerHour, bool Feasible, string? Problem)
{
    public double GainTph { get; init; }
}

/// <summary>Esito dell'analisi rispetto all'obiettivo di produttività.</summary>
public sealed class AdvisorReport
{
    public double TargetTonnesPerHour { get; init; }
    public double BaselineTonnesPerHour { get; init; }
    public double BaselinePackagesPerHour { get; init; }
    public string Bottleneck { get; init; } = string.Empty;
    /// <summary>Tutte le modifiche valutate sul progetto di partenza, dalla migliore.</summary>
    public List<AdvisorOption> Options { get; } = new();
    /// <summary>Modifiche scelte una dopo l'altra (ognuna valutata sul progetto con le precedenti già applicate).</summary>
    public List<AdvisorOption> Path { get; } = new();
    public double BestTonnesPerHour { get; set; }
    public bool TargetReached => TargetTonnesPerHour > 0 && BestTonnesPerHour >= TargetTonnesPerHour - 1e-9;
    /// <summary>Progetto con le modifiche del percorso applicate (null se nessuna modifica migliora).</summary>
    public LineProject? BestProject { get; set; }
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Guida al raggiungimento dell'obiettivo di produttività (t/h) sul mix di pacchi in ingresso: simula il progetto,
/// individua il collo di bottiglia e prova le modifiche ammesse (layout e macchine, logiche; non velocità e rampe),
/// scegliendo una modifica alla volta quella che dà più t/h, finché l'obiettivo è raggiunto o non si migliora più.
/// Ogni proposta deve funzionare per tutti i pacchi della simulazione (tutti legati, nessuna collisione).
/// </summary>
public static class ProductivityAdvisor
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private sealed record Candidate(string Id, string Title, string Description, Func<LineProject, bool> Apply);

    public static AdvisorReport Analyze(LineProject source, double targetTph, int maxSteps = 3, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var current = Prepared(ProjectSerializer.Clone(source));
        progress?.Report("simulazione del progetto attuale");
        var (baseTph, basePph, baseOk, baseProblem, bottleneck) = Evaluate(current);
        var report = new AdvisorReport
        {
            TargetTonnesPerHour = targetTph, BaselineTonnesPerHour = baseTph, BaselinePackagesPerHour = basePph,
            Bottleneck = bottleneck, BestTonnesPerHour = baseOk ? baseTph : 0
        };
        double best = report.BestTonnesPerHour;
        for (int step = 0; step < maxSteps; step++)
        {
            if (report.TargetReached && step > 0) break;
            var results = new List<(AdvisorOption Opt, LineProject Proj)>();
            foreach (var c in Candidates(current))
            {
                ct.ThrowIfCancellationRequested();
                var trial = ProjectSerializer.Clone(current);
                if (!c.Apply(trial)) continue;
                trial = Prepared(trial);
                progress?.Report($"passo {step + 1}: {c.Title}");
                var (tph, pph, ok, problem, _) = Evaluate(trial);
                var opt = new AdvisorOption(c.Id, c.Title, c.Description, tph, pph, ok, problem) { GainTph = ok ? tph - best : 0 };
                results.Add((opt, trial));
            }
            if (step == 0) report.Options.AddRange(results.Select(r => r.Opt).OrderByDescending(o => o.Feasible).ThenByDescending(o => o.TonnesPerHour));
            if (report.TargetReached) break;   // obiettivo già raggiunto dal progetto attuale: solo informazione
            var pick = results.Where(r => r.Opt.Feasible && r.Opt.TonnesPerHour > best * 1.005 + 1e-9).OrderByDescending(r => r.Opt.TonnesPerHour).FirstOrDefault();
            if (pick.Proj == null) break;
            report.Path.Add(pick.Opt);
            current = pick.Proj;
            best = pick.Opt.TonnesPerHour;
            report.BestTonnesPerHour = best;
            report.BestProject = current;
            if (report.TargetReached) break;
        }
        if (!baseOk && report.Path.Count == 0) report.BestTonnesPerHour = 0;
        report.Summary = Describe(report, baseOk, baseProblem);
        return report;
    }

    /// <summary>Regole di layout e controlli di avvio applicati come nell'app prima di simulare.</summary>
    private static LineProject Prepared(LineProject p)
    {
        ProjectAutoFix.Run(p);                 // momento unico dei controlli, come all'avvio della simulazione
        return p;
    }

    /// <summary>Pacchi minimi per misurare la produttività.</summary>
    public const int MeasurePackages = 30;

    private static (double Tph, double Pph, bool Ok, string? Problem, string Bottleneck) Evaluate(LineProject p)
    {
        // stessa misura della simulazione nell'app (sessione A, 2026-11-10): abbastanza pacchi da togliere l'effetto di
        // avvio e di fine linea dalla media dei tempi ciclo, che nell'app (simulazione continua) non c'è
        var q = ProjectSerializer.Clone(p);
        q.Infeed.PackageCount = Math.Max(q.Infeed.PackageCount, MeasurePackages);
        var e = new SimulationEngine(q);
        e.RunToCompletion();
        var r = e.ComputeResults();
        string? problem = null;
        bool strapped = e.Packages.All(k => k.StrapStatus is StrapStatus.Done or StrapStatus.NotRequired);
        if (!e.CompletedNormally) problem = e.FinishReason;
        else if (!strapped) problem = "non tutti i pacchi sono stati legati";
        else if (e.Collisions > 0) problem = $"{e.Collisions} collisioni";
        bool ok = problem == null;
        string b = r.BottleneckId;
        var topo = new LineTopology(p);
        string? stationZone = topo.Zone(p.StrappingStation.InverterId)?.Id;
        string label = b == "STATION" || (stationZone != null && b == stationZone) ? $"via di legatura ({stationZone ?? b}): tempi di legatura e posizionamenti"
            : topo.StopperZone != null && b == topo.StopperZone.Id ? $"via del trasferitore ({b})" : b;
        return (r.TonnesPerHour ?? 0, r.PackagesPerHour ?? 0, ok, problem, label);
    }

    // ------------------------------------------------------------------ Modifiche ammesse (layout e macchine, logiche)

    private static IEnumerable<Candidate> Candidates(LineProject p)
    {
        var st = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).ToList();
        // Legatrici: modifiche minime. Si possono solo aggiungere, sullo stesso lato delle altre, al passo di default
        // (o allo stesso passo delle due già presenti); mai spostare le esistenti né metterle sul lato opposto.
        if (st.Count > 0 && p.Strappers.Count < LineProject.MaxStrappers)
        {
            double pitch = st.Count >= 2 ? st[^1].PositionMm - st[^2].PositionMm : SampleProjects.StrapperSpacingMm;
            yield return new Candidate("ADD_STRAPPER", "Aggiungi una legatrice",
                $"Una legatrice in più (stesso tipo e stesso lato) a {pitch.ToString("0", It)} mm dall'ultima, con il suo stringifascio; le legatrici esistenti non si spostano. La via di legatura si allunga se servono i due rulli dopo l'ultima.",
                q =>
                {
                    var last = q.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).Last();
                    var n = ProjectSerializer.Clone(q).Strappers.First(s => s.Id == last.Id);
                    int k = 1; while (q.Strappers.Any(s => s.Id == "L" + k)) k++;
                    n.Id = "L" + k; n.Name = (last.MachineType == StrapperMachineType.SMA ? "Reggettatrice " : "Legatrice ") + k;
                    n.PositionMm = last.PositionMm + pitch;
                    n.Side = last.Side;
                    q.Strappers.Add(n);
                    StrapperTrack.EnsureMinimumTrack(q);
                    return true;
                });
        }
        foreach (var m in StrappingAreaRules.ReleaseModules(p).Where(m => !m.FollowsStrapping)
                     .Where(m => StrappingAreaRules.RequiredStrappingHeadMaxMm(p) > (StrappingAreaRules.StrappingModule(p)?.EndMm ?? double.MaxValue) + 1e-6))
            yield return new Candidate("SWITCHABLE_" + m.Id, $"Motori commutabili sullo svincolo {m.Id}",
                $"Lo svincolo {m.Id} ha il suo inverter; i motori vengono commutati sull'inverter di legatura solo mentre il pacco in legatura lo impegna per le legature di coda. Negli altri momenti porta via il pacco già legato.",
                q => { var s = q.Sections.First(x => x.Id == m.Id); s.FollowsStrapping = true; return true; });
        if (BankingOptions.ForSimulation(p).Banking == AccumulationMode.SingleBanking)
            yield return new Candidate("DOUBLE_BANKING", "Double banking",
                "Il trasferitore preleva due pacchi per ciclo (il secondo sul fermo scomparente o fermato per posizionamento).",
                q => { BankingOptions.SetBankingAll(q, AccumulationMode.DoubleBanking); return true; });
        if (BankingOptions.ForSimulation(p).Banking == AccumulationMode.SingleBanking && !p.IntermediateStops.Any(s => s.IsEnabled))
            yield return new Candidate("STOP_DOUBLE", "Fermo scomparente al centro + double banking",
                "Un fermo scomparente al centro del trasferitore e due pacchi per ciclo.",
                q =>
                {
                    q.IntermediateStops.Add(new IntermediateStopConfig
                    {
                        Id = "FM1", Name = "Fermo scomparente", Placement = DevicePlacement.Center,
                        PositionMm = StrappingAreaRules.StopPositionMm(q, DevicePlacement.Center), AlignmentTimeS = 2.0
                    });
                    BankingOptions.SetBankingAll(q, AccumulationMode.DoubleBanking);
                    return true;
                });
        if (p.StrappingStation.Strategy != StrappingStrategyMode.Automatic)
            yield return new Candidate("STRATEGY_AUTO", "Piano di legatura ottimizzato",
                "Piano di legatura calcolato dall'ottimizzatore (tutte le combinazioni ammissibili) invece di quello attuale.",
                q => { q.StrappingStation.Strategy = StrappingStrategyMode.Automatic; return true; });
        if (p.Infeed.Mode == InfeedMode.AfterStrapping && p.Infeed.NewPackageDelayS > 5 + 1e-9)
            yield return new Candidate("DELAY_5", "Ritardo di creazione del nuovo pacco a 5 s",
                $"Il nuovo pacco si crea 5 s (invece di {p.Infeed.NewPackageDelayS.ToString("0.#", It)} s) dopo che la coda del precedente ha liberato la fotocellula di ingresso.",
                q => { q.Infeed.NewPackageDelayS = 5; return true; });
        if (p.Infeed.Mode == InfeedMode.AfterStrapping && p.Infeed.NewPackageDelayS > 1e-9)
            yield return new Candidate("DELAY_0", "Nessun ritardo di creazione del nuovo pacco",
                "Il nuovo pacco si crea appena la coda del precedente ha liberato la fotocellula di ingresso.",
                q => { q.Infeed.NewPackageDelayS = 0; return true; });
    }

    // ------------------------------------------------------------------ Testo del rapporto

    private static string Describe(AdvisorReport r, bool baseOk, string? baseProblem)
    {
        string T(double v) => v.ToString("0.0", It);
        var sb = new StringBuilder();
        sb.AppendLine($"Obiettivo: {T(r.TargetTonnesPerHour)} t/h.");
        sb.AppendLine(baseOk
            ? $"Progetto attuale: {T(r.BaselineTonnesPerHour)} t/h ({T(r.BaselinePackagesPerHour)} pacchi/ora). Collo di bottiglia: {r.Bottleneck}."
            : $"Progetto attuale: la simulazione non va a buon fine ({baseProblem}).");
        if (baseOk && r.BaselineTonnesPerHour >= r.TargetTonnesPerHour && r.TargetTonnesPerHour > 0)
            sb.AppendLine("L'obiettivo è già raggiunto.");
        sb.AppendLine();
        sb.AppendLine("Modifiche provate sul progetto attuale:");
        foreach (var o in r.Options)
            sb.AppendLine(o.Feasible
                ? $"• {o.Title}: {T(o.TonnesPerHour)} t/h ({(o.GainTph >= 0 ? "+" : "")}{T(o.GainTph)})"
                : $"• {o.Title}: non applicabile ({o.Problem})");
        sb.AppendLine();
        if (r.Path.Count == 0) sb.AppendLine("Nessuna modifica migliora la produttività.");
        else
        {
            sb.AppendLine("Percorso proposto:");
            for (int i = 0; i < r.Path.Count; i++)
                sb.AppendLine($"{i + 1}. {r.Path[i].Title} → {T(r.Path[i].TonnesPerHour)} t/h. {r.Path[i].Description}");
        }
        if (r.TargetTonnesPerHour > 0)
            sb.AppendLine(r.TargetReached
                ? $"Con il percorso proposto l'obiettivo è raggiunto ({T(r.BestTonnesPerHour)} t/h)."
                : $"Obiettivo non raggiunto: il massimo trovato è {T(r.BestTonnesPerHour)} t/h ({T(r.BestTonnesPerHour - r.TargetTonnesPerHour)} t/h rispetto all'obiettivo). Servono modifiche fuori dalle leve ammesse (es. velocità, tempi macchina, un secondo trasferitore).");
        return sb.ToString();
    }
}
