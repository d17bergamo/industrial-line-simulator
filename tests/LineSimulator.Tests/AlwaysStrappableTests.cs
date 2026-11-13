using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using LineSimulator.Optimization;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// Regola dell'utente (2026-11-13): un pacco deve essere sempre legabile; l'unico caso di errore è il tipo di legatura
/// (Wire / Still Strapp) senza la sua macchina nella linea.
/// </summary>
public class AlwaysStrappableTests
{
    public static IEnumerable<object[]> Models() => SampleProjects.All.Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(Models))]
    public void EveryPackageTypeOfTheExamples_IsStrappable(string name)
    {
        var p = SampleProjects.All.Single(m => m.Name == name).Factory();
        ProjectAutoFix.Run(p);
        var topo = new LineTopology(p);
        foreach (var t in p.PackageTypes)
        {
            Assert.Null(SimulationPreparation.MissingMachine(p, t));
            var cmp = StrappingPlanner.Compare(p, topo, t);
            Assert.True(cmp.Selected.IsFeasible, $"{t.Id}: {cmp.Selected.InfeasibleReason ?? cmp.Error}");
        }
    }

    [Fact]
    public void SteelStrapWithoutReggettatrice_IsAClearErrorBeforeSimulating()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var t = p.PackageTypes.First(x => p.Infeed.TypeSequence.Contains(x.Id));
        t.BindingType = BindingType.SteelStrap;
        Assert.NotNull(SimulationPreparation.MissingMachine(p, t));
        var r = ProjectAutoFix.Preview(p);
        Assert.Contains(r.Errors, e => e.Contains(t.Id) && e.Contains("Still Strapp") && e.Contains("reggettatrice"));
        Assert.DoesNotContain(r.Errors, e => e.Contains(t.Id) && e.Contains("Sposta le legatrici"));
        Assert.Contains(ProjectValidator.Validate(p).Errors, e => e.Code == "STR_TYPE" && e.Message.Contains("Still Strapp"));
    }
}
