using LineSimulator.Core;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;
using LineSimulator.Optimization;

namespace LineSimulator.Tests;

public class OptimizationTests
{
    private static StrappingProblem StandardProblem(LineProject? p = null)
    {
        p ??= LegacySamples.CreateStandardLine();
        var pb = StrappingPlanner.BuildProblem(p, new LineTopology(p), p.PackageTypes[0], out var err);
        Assert.Null(err);
        return pb!;
    }

    [Fact]
    public void StandardLine_BestIsThreeStrappersInParallel()
    {
        var p = LegacySamples.CreateStandardLine();
        var cmp = StrappingPlanner.Compare(p, new LineTopology(p), p.PackageTypes[0]);
        Assert.Equal(7, cmp.Automatic.EvaluatedCombinations);
        var best = cmp.Selected;
        Assert.True(best.IsFeasible);
        Assert.Single(best.Stops);
        Assert.Equal(3, best.Stops[0].Tasks.Count);
        Assert.Equal(new[] { "L1", "L2", "L3" }, best.UsedStrapperIds);
        Assert.Equal(41.0, best.Stops[0].HeadPositionM, 9);

        // Tempo atteso: ingresso 26→41 m a 0.6 m/s + ciclo KNCA 11.5 s + uscita 41→54 m
        double expected = Kinematics.PointToPointTime(15, 0.6, 0.5, 0.6) + 11.5 + Kinematics.PointToPointTime(13, 0.6, 0.5, 0.6);
        Assert.Equal(expected, best.TotalTimeS, 6);
        Assert.Contains("AUTOMATICA", cmp.SelectionReason);
    }

    [Fact]
    public void StandardLine_SinglesAndPairsAreInfeasible_DefaultIsSlower()
    {
        var pb = StandardProblem();
        var opt = new StrappingOptimizer();
        var res = opt.Optimize(pb);
        Assert.Equal(1, res.FeasibleCombinations);
        var def = opt.DefaultPlan(pb);
        Assert.True(def.IsFeasible);
        Assert.Equal(3, def.Stops.Count);
        Assert.Equal(res.Best!.TotalTimeS + 23, def.TotalTimeS, 6); // 3 cicli KNCA da 11.5 s in sequenza invece che in parallelo
    }

    [Fact]
    public void SingleStrapper_SequentialPlanTimes()
    {
        var p = LegacySamples.CreateSingleStrapperLine();
        var pb = StandardProblem(p);
        var best = new StrappingOptimizer().Optimize(pb).Best!;
        Assert.Equal(new[] { 38.5, 43.0, 47.5 }, best.Stops.Select(s => Math.Round(s.HeadPositionM, 6)));
        double expected = Kinematics.PointToPointTime(12.5, 0.6, 0.5, 0.6)
                        + 2 * Kinematics.PointToPointTime(4.5, 0.3, 0.5, 0.6)
                        + 3 * 11.5
                        + Kinematics.PointToPointTime(48 + 12 - 47.5, 0.6, 0.5, 0.6);
        Assert.Equal(expected, best.TotalTimeS, 6);
    }

    [Fact]
    public void ManualPlan_InvalidGeometryIsRejectedWithReason()
    {
        var pb = StandardProblem();
        var manual = new ManualStrappingPlan
        {
            PackageTypeId = "P12",
            Stops = { new ManualStrappingStop { Assignments = { new StrapAssignment { StrapperId = "L1", StrapIndex = 0 }, new StrapAssignment { StrapperId = "L2", StrapIndex = 1 } } } }
        };
        var plan = new StrappingOptimizer().EvaluateManual(pb, manual);
        Assert.False(plan.IsFeasible);
        Assert.False(string.IsNullOrEmpty(plan.InfeasibleReason));
    }

    [Fact]
    public void NonCombinableStrapper_ExcludedFromCombinations()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Strappers[1].AllowCombinedWork = false;
        var res = new StrappingOptimizer().Optimize(StandardProblem(p));
        Assert.Null(res.Best);
        Assert.All(res.Candidates.Where(c => c.CandidateStrapperIds.Count > 1 && c.CandidateStrapperIds.Contains("L2")),
            c => Assert.False(c.IsFeasible));
    }

    /// <summary>
    /// Confronto con ricerca esaustiva: per istanze casuali (seme fisso) l'ottimo della programmazione
    /// dinamica coincide con il minimo su tutte le assegnazioni legatura→legatrice.
    /// </summary>
    [Fact]
    public void Optimizer_MatchesBruteForce_OnRandomInstances()
    {
        var rnd = new Random(12345);
        var opt = new StrappingOptimizer();
        int compared = 0;
        for (int iter = 0; iter < 300; iter++)
        {
            int m = rnd.Next(1, 4), n = rnd.Next(1, 5);
            var strappers = new List<StrapperSpec>();
            double x = 30 + rnd.Next(0, 4) * 0.5;
            for (int i = 0; i < m; i++)
            {
                strappers.Add(new StrapperSpec($"L{i + 1}", $"L{i + 1}", i, x, rnd.Next(0, 3), 4 + rnd.Next(0, 6), rnd.Next(0, 3), true));
                x += 1.5 + rnd.Next(0, 8) * 0.5;
            }
            var straps = new List<double>();
            double d = 0.5 + rnd.Next(0, 3) * 0.5;
            for (int k = 0; k < n; k++) { straps.Add(d); d += 1.0 + rnd.Next(0, 8) * 0.5; }
            double L = d + 0.5;
            var pb = new StrappingProblem
            {
                Strappers = strappers, StrapPositionsM = straps, PackageLengthM = L,
                HeadMinM = 28 + L * 0.3, HeadMaxM = 28 + L + 8, ReferenceHeadM = 26, ExitHeadM = 28 + L + 8 + L,
                TransportSpeed = 0.6, PositioningSpeed = 0.3, Acceleration = 0.5, Deceleration = 0.6
            };
            var dp = opt.Optimize(pb).Best;
            double brute = BruteForce(pb);
            if (double.IsPositiveInfinity(brute)) { Assert.Null(dp); continue; }
            Assert.NotNull(dp);
            Assert.Equal(brute, dp!.TotalTimeS, 6);
            compared++;
        }
        Assert.True(compared > 50, $"Solo {compared} istanze ammissibili");
    }

    private static double BruteForce(StrappingProblem pb)
    {
        int m = pb.Strappers.Count, n = pb.StrapPositionsM.Count;
        double best = double.PositiveInfinity;
        int total = (int)Math.Pow(m, n);
        var assign = new int[n];
        for (int code = 0; code < total; code++)
        {
            int c = code;
            for (int k = 0; k < n; k++) { assign[k] = c % m; c /= m; }
            var hs = new List<(double H, int S)>();
            bool ok = true;
            for (int k = 0; k < n && ok; k++)
            {
                double h = pb.Strappers[assign[k]].PositionM + pb.StrapPositionsM[k];
                if (h < pb.HeadMinM - pb.MatchToleranceM || h > pb.HeadMaxM + pb.MatchToleranceM) ok = false;
                hs.Add((h, assign[k]));
            }
            if (!ok) continue;
            // raggruppa per posizione: stessa posizione ⇒ parallelo (una legatrice per fermata)
            var groups = new List<(double H, List<int> S)>();
            foreach (var (h, s) in hs.OrderBy(t => t.H))
            {
                if (groups.Count > 0 && Math.Abs(groups[^1].H - h) <= pb.MatchToleranceM) groups[^1].S.Add(s);
                else groups.Add((h, new List<int> { s }));
            }
            if (groups.Any(g => g.S.Distinct().Count() != g.S.Count)) continue; // stessa legatrice due volte nella stessa fermata
            double t = 0, last = pb.ReferenceHeadM;
            for (int i = 0; i < groups.Count; i++)
            {
                double dist = groups[i].H - last;
                t += i == 0 ? Kinematics.PointToPointTime(Math.Max(0, dist), pb.TransportSpeed, pb.Acceleration, pb.Deceleration)
                            : Kinematics.PointToPointTime(dist, pb.PositioningSpeed, pb.Acceleration, pb.Deceleration);
                t += groups[i].S.Max(s => pb.Strappers[s].CycleS);
                last = groups[i].H;
            }
            t += Kinematics.PointToPointTime(Math.Max(0, pb.ExitHeadM - last), pb.TransportSpeed, pb.Acceleration, pb.Deceleration);
            best = Math.Min(best, t);
        }
        return best;
    }

    [Fact]
    public void StrapperCycle_PhasesAndDuration()
    {
        var cfg = new StrapperConfig { Id = "L1", PreparationTimeS = 2, StrappingTimeS = 8, ReturnTimeS = 2 };
        var s = new StrapperRuntime(cfg, 0);
        var pkg = new PackageRuntime(1, new PackageType { Id = "T", LengthMm = 12000, WeightKg = 1000 });
        s.Start(pkg, 0);
        var seen = new List<StrapperState> { s.State };
        int steps = 0;
        while (s.Busy && steps < 100000)
        {
            var ch = s.Advance(0.01);
            if (ch != null) seen.Add(ch.Value);
            steps++;
        }
        Assert.Equal(new[] { StrapperState.Preparing, StrapperState.Strapping, StrapperState.Returning, StrapperState.Idle }, seen);
        Assert.InRange(steps * 0.01, 12 - 1e-6, 12 + 0.011);
        Assert.Equal(1, s.CompletedCycles);
        Assert.Equal(12, cfg.CycleTimeS, 9);
        Assert.Throws<InvalidOperationException>(() => { s.Start(pkg, 0); s.Start(pkg, 1); });
    }
}
