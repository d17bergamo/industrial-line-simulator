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
}
