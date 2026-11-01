using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Optimization;

public sealed class OptimizationResult
{
    public List<StrappingPlan> Candidates { get; } = new();
    public StrappingPlan? Best { get; internal set; }
    public int EvaluatedCombinations => Candidates.Count;
    public int FeasibleCombinations => Candidates.Count(c => c.IsFeasible);
}

/// <summary>
/// Ottimizzatore esatto della sequenza di legatura.
///
/// Per ogni combinazione di legatrici (singole, coppie, terna) si risolve con programmazione
/// dinamica il problema: scegliere una sequenza crescente di posizioni di arresto della testa del pacco
/// e, per ciascuna, un insieme di legature da eseguire in parallelo, coprendo ogni legatura
/// esattamente una volta e usando ogni legatrice della combinazione almeno una volta,
/// minimizzando il tempo totale (spostamenti con profilo trapezoidale + lavorazioni).
/// Stato DP: (fermata candidata, legature eseguite, legatrici usate). La soluzione è ottima
/// (nessuna euristica, nessuna casualità). Pareggi: meno fermate, poi meno legatrici, poi meno
/// tempo-macchina complessivo, poi ordine degli identificativi (deterministico).
/// </summary>
public sealed class StrappingOptimizer
{
    public const string AutomaticName = "Automatica ottimizzata";
    public const string DefaultName = "Predefinita";
    public const string ManualName = "Manuale";
    private const double Eps = 1e-9;

    public OptimizationResult Optimize(StrappingProblem pb)
    {
        var result = new OptimizationResult();
        var list = pb.Strappers;
        int m = list.Count;
        for (int bits = 1; bits < (1 << m); bits++)
        {
            var subset = new List<StrapperSpec>();
            for (int i = 0; i < m; i++) if ((bits & (1 << i)) != 0) subset.Add(list[i]);
            string name = $"{AutomaticName} [{string.Join("+", subset.Select(s => s.Id))}]";
            if (subset.Count > 1 && subset.Any(s => !s.AllowCombined))
            {
                result.Candidates.Add(StrappingPlan.Infeasible(name,
                    "una legatrice della combinazione non può lavorare in combinazione", subset.Select(s => s.Id).ToList()));
                continue;
            }
            result.Candidates.Add(OptimizeSubset(pb, subset, true, name));
        }
        result.Best = result.Candidates.Where(c => c.IsFeasible)
            .OrderBy(c => c.TotalTimeS)
            .ThenBy(c => c.Stops.Count)
            .ThenBy(c => c.UsedStrapperIds.Count)
            .ThenBy(c => c.TotalStrapperBusyS)
            .ThenBy(c => string.Join("+", c.UsedStrapperIds), StringComparer.Ordinal)
            .FirstOrDefault();
        return result;
    }

    private sealed record Candidate(double H, List<(int S, int K)> Pairs);
    private sealed record Option(int StrapMask, int StrapperMask, double Work, List<(int S, int K)> Pairs);

    /// <summary>Ottimo esatto per una data combinazione di legatrici.</summary>
    public StrappingPlan OptimizeSubset(StrappingProblem pb, IReadOnlyList<StrapperSpec> subset, bool requireAllUsed, string name)
    {
        var ids = subset.Select(s => s.Id).ToList();
        int n = pb.StrapPositionsM.Count;
        int m = subset.Count;
        if (n == 0) return new StrappingPlan { StrategyName = name, IsFeasible = true, CandidateStrapperIds = ids, Stops = new() };
        if (n > 16) return StrappingPlan.Infeasible(name, "troppe legature per l'ottimizzazione esatta (max 16)", ids);

        // 1) Fermate candidate: H = X_s + d_k, raggruppate entro la tolleranza
        var raw = new List<(double H, int S, int K)>();
        for (int s = 0; s < m; s++)
            for (int k = 0; k < n; k++)
            {
                double h = subset[s].PositionM + pb.StrapPositionsM[k];
                if (h >= pb.HeadMinM - pb.MatchToleranceM && h <= pb.HeadMaxM + pb.MatchToleranceM && h >= pb.ReferenceHeadM - pb.MatchToleranceM)
                    raw.Add((h, s, k));
            }
        raw.Sort((a, b) => a.H != b.H ? a.H.CompareTo(b.H) : a.S != b.S ? a.S.CompareTo(b.S) : a.K.CompareTo(b.K));
        var cands = new List<Candidate>();
        foreach (var r in raw)
        {
            if (cands.Count > 0 && Math.Abs(cands[^1].H - r.H) <= pb.MatchToleranceM
                && !cands[^1].Pairs.Any(p => p.S == r.S || p.K == r.K))
                cands[^1].Pairs.Add((r.S, r.K));
            else
                cands.Add(new Candidate(r.H, new List<(int, int)> { (r.S, r.K) }));
        }

        // Verifica di raggiungibilità di ciascuna legatura
        for (int k = 0; k < n; k++)
            if (!cands.Any(c => c.Pairs.Any(p => p.K == k)))
                return StrappingPlan.Infeasible(name,
                    $"legatura #{k + 1} ({pb.StrapPositionsM[k] * 1000:0} mm) non raggiungibile: nessuna legatrice la porta con il pacco interamente nella zona", ids);

        // 2) Opzioni per fermata (sottoinsiemi non vuoti delle coppie disponibili)
        var options = new List<List<Option>>();
        foreach (var c in cands)
        {
            var opts = new List<Option>();
            int pc = c.Pairs.Count;
            for (int b = 1; b < (1 << pc); b++)
            {
                var chosen = new List<(int S, int K)>();
                for (int i = 0; i < pc; i++) if ((b & (1 << i)) != 0) chosen.Add(c.Pairs[i]);
                if (chosen.Count > 1 && chosen.Any(x => !subset[x.S].AllowCombined)) continue;
                int sm = 0, km = 0; double w = 0;
                foreach (var (s, k) in chosen) { sm |= 1 << s; km |= 1 << k; w = Math.Max(w, subset[s].CycleS); }
                opts.Add(new Option(km, sm, w, chosen));
            }
            options.Add(opts);
        }

        int C = cands.Count, NM = 1 << n, SM = 1 << m;
        var dp = new double[C, NM, SM];
        var stops = new int[C, NM, SM];
        var prev = new (int C, int Mask, int SMask, int Opt)[C, NM, SM];
        for (int c = 0; c < C; c++)
            for (int a = 0; a < NM; a++)
                for (int b = 0; b < SM; b++) { dp[c, a, b] = double.PositiveInfinity; prev[c, a, b] = (-1, 0, 0, -1); }

        double MoveFirst(double d) => Kinematics.PointToPointTime(d, pb.TransportSpeed, pb.Acceleration, pb.Deceleration);
        double MoveBetween(double d) => Kinematics.PointToPointTime(d, pb.PositioningSpeed, pb.Acceleration, pb.Deceleration);

        bool Better(double t, int st, double bt, int bst) => t < bt - Eps || (Math.Abs(t - bt) <= Eps && st < bst);

        for (int c = 0; c < C; c++)
        {
            double t0 = MoveFirst(Math.Max(0, cands[c].H - pb.ReferenceHeadM));
            for (int o = 0; o < options[c].Count; o++)
            {
                var op = options[c][o];
                double t = t0 + op.Work;
                if (Better(t, 1, dp[c, op.StrapMask, op.StrapperMask], stops[c, op.StrapMask, op.StrapperMask]))
                {
                    dp[c, op.StrapMask, op.StrapperMask] = t;
                    stops[c, op.StrapMask, op.StrapperMask] = 1;
                    prev[c, op.StrapMask, op.StrapperMask] = (-1, 0, 0, o);
                }
            }
        }

        for (int c = 0; c < C; c++)
            for (int a = 0; a < NM; a++)
                for (int b = 0; b < SM; b++)
                {
                    double cur = dp[c, a, b];
                    if (double.IsPositiveInfinity(cur)) continue;
                    for (int c2 = c + 1; c2 < C; c2++)
                    {
                        double mv = MoveBetween(cands[c2].H - cands[c].H);
                        for (int o = 0; o < options[c2].Count; o++)
                        {
                            var op = options[c2][o];
                            if ((op.StrapMask & a) != 0) continue;
                            int na = a | op.StrapMask, nb = b | op.StrapperMask;
                            double t = cur + mv + op.Work;
                            int st = stops[c, a, b] + 1;
                            if (Better(t, st, dp[c2, na, nb], stops[c2, na, nb] == 0 ? int.MaxValue : stops[c2, na, nb]))
                            {
                                dp[c2, na, nb] = t;
                                stops[c2, na, nb] = st;
                                prev[c2, na, nb] = (c, a, b, o);
                            }
                        }
                    }
                }

        // 3) Chiusura: tutte le legature, (eventualmente) tutte le legatrici, uscita
        int full = NM - 1, fullS = SM - 1;
        double best = double.PositiveInfinity; int bestStops = int.MaxValue; int bc = -1, bsm = -1;
        for (int c = 0; c < C; c++)
            for (int b = 0; b < SM; b++)
            {
                if (requireAllUsed && b != fullS) continue;
                double cur = dp[c, full, b];
                if (double.IsPositiveInfinity(cur)) continue;
                double t = cur + MoveFirst(Math.Max(0, pb.ExitHeadM - cands[c].H));
                if (Better(t, stops[c, full, b], best, bestStops)) { best = t; bestStops = stops[c, full, b]; bc = c; bsm = b; }
            }
        if (bc < 0)
            return StrappingPlan.Infeasible(name, requireAllUsed && m > 1
                ? "nessuna sequenza ammissibile che utilizzi tutte le legatrici della combinazione"
                : "nessuna sequenza ammissibile", ids);

        // 4) Ricostruzione
        var chain = new List<(int C, int Opt)>();
        int cc = bc, ca = full, cb = bsm;
        while (cc >= 0)
        {
            var pv = prev[cc, ca, cb];
            chain.Add((cc, pv.Opt));
            (cc, ca, cb) = (pv.C, pv.Mask, pv.SMask);
        }
        chain.Reverse();

        var planStops = new List<PlannedStop>();
        double lastH = pb.ReferenceHeadM;
        for (int i = 0; i < chain.Count; i++)
        {
            var cand = cands[chain[i].C];
            var op = options[chain[i].C][chain[i].Opt];
            double mvTime = i == 0 ? MoveFirst(Math.Max(0, cand.H - lastH)) : MoveBetween(cand.H - lastH);
            planStops.Add(new PlannedStop
            {
                HeadPositionM = cand.H,
                MoveTimeS = mvTime,
                Tasks = op.Pairs.OrderBy(p => subset[p.S].PositionM)
                    .Select(p => new StrapTask(subset[p.S].Id, p.K, subset[p.S].CycleS)).ToList()
            });
            lastH = cand.H;
        }
        return new StrappingPlan
        {
            StrategyName = name,
            IsFeasible = true,
            Stops = planStops,
            ExitMoveTimeS = MoveFirst(Math.Max(0, pb.ExitHeadM - lastH)),
            CandidateStrapperIds = ids
        };
    }

    /// <summary>
    /// Strategia predefinita (assegnazione fissa, senza ottimizzazione): una legatura per fermata,
    /// ciascuna eseguita dalla prima legatrice (in ordine di configurazione) che la raggiunge.
    /// </summary>
    public StrappingPlan DefaultPlan(StrappingProblem pb)
    {
        var tasks = new List<(double H, StrapperSpec S, int K)>();
        for (int k = 0; k < pb.StrapPositionsM.Count; k++)
        {
            StrapperSpec? chosen = null; double h = 0;
            foreach (var s in pb.Strappers.OrderBy(s => s.Index))
            {
                h = s.PositionM + pb.StrapPositionsM[k];
                if (h >= pb.HeadMinM - pb.MatchToleranceM && h <= pb.HeadMaxM + pb.MatchToleranceM) { chosen = s; break; }
            }
            if (chosen == null)
                return StrappingPlan.Infeasible(DefaultName, $"legatura #{k + 1} non raggiungibile da alcuna legatrice");
            tasks.Add((h, chosen, k));
        }
        tasks.Sort((a, b) => a.H != b.H ? a.H.CompareTo(b.H) : a.K.CompareTo(b.K));
        var stops = new List<PlannedStop>();
        double last = pb.ReferenceHeadM;
        foreach (var t in tasks)
        {
            double mv = stops.Count == 0
                ? Kinematics.PointToPointTime(Math.Max(0, t.H - last), pb.TransportSpeed, pb.Acceleration, pb.Deceleration)
                : Kinematics.PointToPointTime(t.H - last, pb.PositioningSpeed, pb.Acceleration, pb.Deceleration);
            stops.Add(new PlannedStop { HeadPositionM = t.H, MoveTimeS = mv, Tasks = new() { new StrapTask(t.S.Id, t.K, t.S.CycleS) } });
            last = t.H;
        }
        return new StrappingPlan
        {
            StrategyName = DefaultName,
            IsFeasible = true,
            Stops = stops,
            ExitMoveTimeS = Kinematics.PointToPointTime(Math.Max(0, pb.ExitHeadM - last), pb.TransportSpeed, pb.Acceleration, pb.Deceleration),
            CandidateStrapperIds = pb.Strappers.Select(s => s.Id).ToList()
        };
    }

    /// <summary>Valuta (e verifica) un piano inserito manualmente.</summary>
    public StrappingPlan EvaluateManual(StrappingProblem pb, ManualStrappingPlan manual)
    {
        int n = pb.StrapPositionsM.Count;
        var done = new bool[n];
        var stops = new List<PlannedStop>();
        double last = pb.ReferenceHeadM;
        int idx = 0;
        foreach (var ms in manual.Stops)
        {
            idx++;
            if (ms.Assignments.Count == 0) return StrappingPlan.Infeasible(ManualName, $"fermata {idx} senza assegnazioni");
            double? h = null;
            var tasks = new List<StrapTask>();
            foreach (var a in ms.Assignments)
            {
                var s = pb.Strappers.FirstOrDefault(x => x.Id == a.StrapperId);
                if (s == null) return StrappingPlan.Infeasible(ManualName, $"fermata {idx}: legatrice '{a.StrapperId}' inesistente o disabilitata");
                if (a.StrapIndex < 0 || a.StrapIndex >= n) return StrappingPlan.Infeasible(ManualName, $"fermata {idx}: legatura #{a.StrapIndex + 1} inesistente");
                if (done[a.StrapIndex]) return StrappingPlan.Infeasible(ManualName, $"legatura #{a.StrapIndex + 1} assegnata più volte");
                if (tasks.Any(t => t.StrapperId == s.Id)) return StrappingPlan.Infeasible(ManualName, $"fermata {idx}: {s.Id} usata due volte nella stessa fermata");
                double hh = s.PositionM + pb.StrapPositionsM[a.StrapIndex];
                if (h == null) h = hh;
                else if (Math.Abs(h.Value - hh) > pb.MatchToleranceM)
                    return StrappingPlan.Infeasible(ManualName, $"fermata {idx}: le legature assegnate richiedono posizioni del pacco diverse ({h.Value * 1000:0} / {hh * 1000:0} mm)");
                done[a.StrapIndex] = true;
                tasks.Add(new StrapTask(s.Id, a.StrapIndex, s.CycleS));
            }
            if (tasks.Count > 1 && tasks.Any(t => !pb.Strappers.First(s => s.Id == t.StrapperId).AllowCombined))
                return StrappingPlan.Infeasible(ManualName, $"fermata {idx}: legatrice non abilitata al lavoro combinato");
            double H = h!.Value;
            if (H < pb.HeadMinM - pb.MatchToleranceM || H > pb.HeadMaxM + pb.MatchToleranceM)
                return StrappingPlan.Infeasible(ManualName, $"fermata {idx}: testa a {H * 1000:0} mm fuori dal campo ammesso [{pb.HeadMinM * 1000:0}; {pb.HeadMaxM * 1000:0}] mm");
            if (stops.Count > 0 && H < last - pb.MatchToleranceM)
                return StrappingPlan.Infeasible(ManualName, $"fermata {idx}: il pacco dovrebbe arretrare (solo avanzamento ammesso)");
            double mv = stops.Count == 0
                ? Kinematics.PointToPointTime(Math.Max(0, H - last), pb.TransportSpeed, pb.Acceleration, pb.Deceleration)
                : Kinematics.PointToPointTime(Math.Max(0, H - last), pb.PositioningSpeed, pb.Acceleration, pb.Deceleration);
            stops.Add(new PlannedStop { HeadPositionM = H, MoveTimeS = mv, Tasks = tasks });
            last = H;
        }
        for (int k = 0; k < n; k++)
            if (!done[k]) return StrappingPlan.Infeasible(ManualName, $"legatura #{k + 1} non assegnata");
        return new StrappingPlan
        {
            StrategyName = ManualName,
            IsFeasible = true,
            Stops = stops,
            ExitMoveTimeS = Kinematics.PointToPointTime(Math.Max(0, pb.ExitHeadM - last), pb.TransportSpeed, pb.Acceleration, pb.Deceleration),
            CandidateStrapperIds = pb.Strappers.Select(s => s.Id).ToList()
        };
    }
}
