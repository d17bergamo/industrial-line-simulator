using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Modelli dell'area di legatura, regole di layout, ingresso "un pacco alla volta" e bilico senza movimento.</summary>
public class LineModelsTests
{
    private static string Diag(SimulationEngine e) =>
        $"{e.FinishReason} t={e.Time:0.0}\n" + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(30));

    public static IEnumerable<object[]> Models() => SampleProjects.All.Select(m => new object[] { m.Name });
    private static LineProject Model(string name) => SampleProjects.All.Single(m => m.Name == name).Factory();

    [Fact]
    public void FourModels_WithTheRequestedStrappersAndPackageRanges()
    {
        var expected = new (int Strappers, int[] Lengths)[] { (3, new[] { 6, 9, 12, 18, 24 }), (2, new[] { 6, 9, 12, 18 }), (2, new[] { 6, 9, 12 }), (1, new[] { 6, 9, 12 }) };
        Assert.Equal(4, SampleProjects.All.Count);
        for (int i = 0; i < 4; i++)
        {
            var p = SampleProjects.All[i].Factory();
            Assert.Equal(expected[i].Strappers, p.Strappers.Count);
            Assert.Equal(expected[i].Lengths.Select(m => $"P{m} (Wire)"), p.Infeed.TypeSequence);
            Assert.Equal(expected[i].Lengths.Select(m => m * 1000.0), p.PackageTypes.Select(t => t.LengthMm));   // solo le tipologie del nome dell'esempio
        }
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Model_FollowsTheLayoutRules(string name)
    {
        var p = Model(name);
        var first = p.Sections.OrderBy(s => s.StartMm).First();
        var topo = new LineTopology(p);
        // Modulo di legatura sempre il primo, con tutte le legatrici e la fotocellula di ingresso
        Assert.Same(topo.Zone(first.Id), topo.Zone(p.StrappingStation.InverterId));
        Assert.All(p.Strappers, s => Assert.InRange(s.PositionMm, first.StartMm, first.EndMm));
        Assert.InRange(p.Photocells.Single(x => x.IsEntryPhotocell).PositionMm, first.StartMm, first.StartMm + 1000);
        // Legatrici a 2500 mm
        var pos = p.Strappers.OrderBy(s => s.PositionMm).Select(s => s.PositionMm).ToList();
        for (int i = 1; i < pos.Count; i++) Assert.Equal(2500, pos[i] - pos[i - 1], 6);
        // Uno stringifascio per ogni legatrice, 450 mm a monte
        Assert.True(p.BundleClamp.IsEnabled && p.BundleClamp.PerMachine);
        Assert.Equal(pos.Select(x => x - 450), LayoutRules.BundleClampPositions(p).Select(k => k.PositionMm));
        // Almeno 3 m dal trasferitore; via rulli davanti al trasferitore lunga come il trasferitore
        Assert.All(p.Strappers, s => Assert.True(p.Transfer.PositionMm - s.PositionMm >= 3000));
        Assert.True(LayoutRules.TransferModuleMatches(p));
        Assert.Contains(p.Photocells, x => x.Purpose == PhotocellPurpose.StrappingExit);
        var v = ProjectValidator.Validate(p);
        Assert.False(v.HasErrors, string.Join(" | ", v.Errors.Select(x => x.Message)));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Model_SimulatesAllPackages_StrappedAndWeighed(string name)
    {
        var p = Model(name);
        var e = new SimulationEngine(p);
        Assert.Empty(e.Preparation.Changes);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(p.Infeed.PackageCount, e.Packages.Count(k => k.State == PackageState.Exited));
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.All(e.Packages, k => Assert.NotNull(k.MeasuredWeightKg));
        Assert.Equal(0, e.Collisions);
        Assert.Contains(e.Events, x => x.Type == SimEventType.BundleClampPhase);
    }

    [Fact]
    public void Infeed_NewPackageCreatedAfterEntryPhotocellPlusDelay_AdvancesWhenPreviousIsStrapped()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Infeed.PackageCount = 4;
        double entry = p.Photocells.Single(x => x.IsEntryPhotocell).PositionMm / 1000.0;
        double exit = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingExit).PositionMm / 1000.0;
        var e = new SimulationEngine(p);
        var clearedEntry = new Dictionary<int, double>();
        var created = new Dictionary<int, double>();
        var moved = new HashSet<int>();
        var together = new HashSet<int>();
        while (e.Step())
        {
            double t = e.Time;
            foreach (var k in e.Packages)
            {
                if (!clearedEntry.ContainsKey(k.Sequence) && (k.TailM > entry || !k.OnRollers)) clearedEntry[k.Sequence] = t;
                if (!created.ContainsKey(k.Sequence))
                {
                    created[k.Sequence] = t;
                    Assert.True(k.HeadM <= p.Sections.Min(s => s.StartMm) / 1000.0 + 0.01);   // nasce a monte della via rulli di legatura (dietro al precedente)
                    if (k.Sequence > 1)
                        Assert.True(t >= clearedEntry[k.Sequence - 1] + p.Infeed.NewPackageDelayS - 0.05,
                            $"{k.Id} creato a {t:0.00} s, la coda del precedente ha liberato l'ingresso a {clearedEntry[k.Sequence - 1]:0.00} s");
                }
                if (!moved.Contains(k.Sequence) && k.HeadM > 1e-6)
                {
                    moved.Add(k.Sequence);
                    if (k.Sequence > 1)
                    {
                        var prev = e.Packages.Single(x => x.Sequence == k.Sequence - 1);
                        // il pacco in attesa su FC0 avanza quando il precedente è legato e la sua coda ha liberato l'uscita legatura
                        Assert.True(!prev.OnRollers || prev.TailM > exit - 0.01,
                            $"{k.Id} avanza con la coda di {prev.Id} a {prev.TailM:0.00} m (uscita {exit:0.00} m)");
                        if (prev.OnRollers && prev.TailM <= exit - 0.01) together.Add(k.Sequence);
                    }
                }
            }
        }
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(4, created.Count);
        // regola del 2026-11-13: il nuovo parte quando il precedente è legato e ha liberato la fotocellula di uscita legatura
        Assert.Empty(together);
    }

    [Fact]
    public void Infeed_DelayIsConfigurable_Default10s()
    {
        Assert.Equal(10, new InfeedConfig().NewPackageDelayS);
        Assert.Equal(InfeedMode.AfterStrapping, new InfeedConfig().Mode);
        var slow = SampleProjects.CreateOneStrapper12m(); slow.Infeed.PackageCount = 3; slow.Infeed.NewPackageDelayS = 60;
        var fast = SampleProjects.CreateOneStrapper12m(); fast.Infeed.PackageCount = 3; fast.Infeed.NewPackageDelayS = 0;
        var a = new SimulationEngine(slow); a.RunToCompletion();
        var b = new SimulationEngine(fast); b.RunToCompletion();
        Assert.True(a.CompletedNormally && b.CompletedNormally);
        Assert.True(a.Packages[1].EntryTimeS > b.Packages[1].EntryTimeS + 50);
    }

    [Fact]
    public void Scale_PackageDoesNotMoveOnTheScale()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Infeed.PackageCount = 3;
        var e = new SimulationEngine(p);
        var headOnScale = new Dictionary<int, double>();
        while (e.Step())
            foreach (var k in e.Packages.Where(k => k.State == PackageState.OnScale))
            {
                if (!headOnScale.TryGetValue(k.Sequence, out var h)) headOnScale[k.Sequence] = k.HeadM;
                else Assert.Equal(h, k.HeadM, 9);
            }
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(3, headOnScale.Count);
        Assert.Contains(e.Events, x => x.Message.Contains("il pacco non si muove sul bilico", StringComparison.Ordinal));
    }

    [Fact]
    public void Strapper_NeverWithin3mOfTheLiftingTransfer()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        p.Strappers[0].PositionMm = p.Transfer.PositionMm - 2000;
        Assert.Contains(ProjectValidator.Validate(p).Errors, x => x.Code == "STR_TRF_MIN");
        var fixes = LayoutRules.Enforce(p);
        Assert.Contains(fixes, f => f.Contains("almeno 3 m dal trasferitore", StringComparison.Ordinal));
        Assert.True(p.Transfer.PositionMm - p.Strappers[0].PositionMm >= 3000 - 1e-6);
        // la legatrice è finita sullo svincolo: la via di legatura ora finisce dopo di lei, e lo svincolo viene reinserito a 1 m dal trasferitore
        Assert.True(StrappingAreaRules.ExitToTransferMm(p) >= 1000 - 1e-6);
        Assert.DoesNotContain(ProjectValidator.Validate(p).Errors, x => x.Code == "STR_TRF_MIN");
    }

    [Fact]
    public void TransferModule_AlwaysAsLongAsTheTransfer()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        p.Transfer.LengthMm = 15000;
        Assert.False(LayoutRules.TransferModuleMatches(p));
        Assert.Contains(ProjectValidator.Validate(p).Issues, x => x.Code == "TRF_MODULE");
        Assert.True(LayoutRules.AlignTransferModule(p));
        var m = LayoutRules.TransferModule(p)!;
        Assert.Equal(15000, m.LengthMm, 6);
        Assert.Equal(p.Transfer.PositionMm, m.StartMm, 6);
        Assert.Equal(m.EndMm, p.Stopper.PositionMm, 6);
    }

    [Fact]
    public void Package_StrapsAreSymmetric()
    {
        foreach (int m in SampleProjects.PackageLengthsM)
        {
            var t = SampleProjects.Package(m);
            var s = t.StrapPositionsMm;
            for (int i = 0; i < s.Count; i++) Assert.Equal(t.LengthMm, s[i] + s[s.Count - 1 - i], 6);
            Assert.All(s, x => Assert.Equal(0, x % 100));
        }
    }
}
