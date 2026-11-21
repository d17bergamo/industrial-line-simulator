using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Un solo calcolo "ci stanno due pacchi" senza fermo scomparente (revisore D2/B1, 2026-11-21).</summary>
public class TwoPackagesFitTests
{
    [Fact]
    public void WithoutStop_TableAndSharedCalculationAgree()
    {
        foreach (var m in SampleProjects.All)
        {
            var p = m.Factory();
            p.IntermediateStops.Clear();
            foreach (var t in p.PackageTypes)
            {
                bool shared = BankingOptions.TwoFitWithoutStop(p, t.LengthMm);
                if (!shared) Assert.False(BankingOptions.DoubleBankingAvailable(p, t), $"{m.Name} {t.Id}: la tabella ammette il double banking ma due pacchi non stanno");
            }
        }
    }

    [Fact]
    public void SecondTail_NeverReachesIntoThePreviousSection()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        double min = BankingOptions.MinSecondTailMm(p);
        var module = LayoutRules.TransferModule(p)!;
        Assert.True(min >= module.StartMm - 1e-6, $"coda minima {min} mm prima dell'inizio del modulo {module.StartMm} mm");
    }

    private static LineProject ThreeStrappers() => SampleProjects.All.Single(m => m.Name.StartsWith("3 legatrici", StringComparison.Ordinal)).Factory();

    [Fact]
    public void DoubleBanking_TransferRoadwayOnTwoInverters_NamedAandB()
    {
        // revisore B13: monte = M3A (secondo pacco), valle = M3B (primo pacco)
        var p = ThreeStrappers();
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        var e = new LineSimulator.Engine.SimulationEngine(p);
        string id = LayoutRules.TransferModule(p)!.Id;
        Assert.True(TransferSplitRule.SplitMm(p) > 0);
        Assert.Equal(id + "A", e.MonteInverterId);
        Assert.Equal(id + "B", e.ValleInverterId);
        Assert.Equal(TransferSplitRule.MonteId(id), e.MonteInverterId);
        var doc = LineSimulator.Infrastructure.ControlLogicGenerator.Generate(p);
        Assert.Contains(doc.Inverters, r => r.Id == id + "A");
        Assert.Contains(doc.Inverters, r => r.Id == id + "B");
    }

    [Fact]
    public void SingleBankingOnly_TransferRoadwayOnOneInverter()
    {
        var p = ThreeStrappers();
        BankingOptions.SetBankingAll(p, AccumulationMode.SingleBanking);
        var e = new LineSimulator.Engine.SimulationEngine(p);
        Assert.Equal(0, TransferSplitRule.SplitMm(p));
        Assert.Null(e.MonteInverterId);
        Assert.Null(e.ValleInverterId);
    }
}
