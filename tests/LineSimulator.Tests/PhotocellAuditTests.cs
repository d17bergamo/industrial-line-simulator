using LineSimulator.Core;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>Controllo delle fotocellule (decisione dell'utente del 2026-11-13, opzione "b"): perché c'è, distanza minima, superflue.</summary>
public class PhotocellAuditTests
{
    public static IEnumerable<object[]> Models() => SampleProjects.All.Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(Models))]
    public void Examples_EveryPhotocellHasAReason_NoneSuperfluous_StopsAtMinimum(string name)
    {
        var p = SampleProjects.All.Single(m => m.Name == name).Factory();
        ProjectAutoFix.Run(p);
        var rows = PhotocellAudit.Run(p);
        Assert.Equal(p.Photocells.Count(x => x.IsEnabled), rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Role)));
        Assert.DoesNotContain(rows, r => r.Verdict is PhotocellVerdict.Superfluous or PhotocellVerdict.BelowMinimum);
        Assert.All(rows.Where(r => r.DistanceMm != null), r => Assert.Equal(PhotocellVerdict.AtMinimum, r.Verdict));
        Assert.Contains(EquipmentCheck.Run(p, fix: false), c => c.Code == "OK");
    }

    [Fact]
    public void SignalPhotocellNobodyUses_IsSuperfluous_UnlessARuleUsesIt()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        ProjectAutoFix.Run(p);
        var extra = new PhotocellConfig { Id = "FC9", Name = "FC9", PositionMm = 10000, Action = PhotocellAction.None };
        p.Photocells.Add(extra);
        Assert.Contains(PhotocellAudit.Run(p), r => r.Photocell == extra && r.Verdict == PhotocellVerdict.Superfluous);
        Assert.Contains(EquipmentCheck.Run(p, fix: false), c => c.Code == "PC_SUPERFLUOUS" && c.Message.Contains("FC9"));
        p.Rules.Add(new LogicRule { Id = "R1", Condition = ConditionText.Parse("PC(FC9)") });
        Assert.Contains(PhotocellAudit.Run(p), r => r.Photocell == extra && r.Verdict == PhotocellVerdict.Ok);
    }

    [Fact]
    public void StopPhotocellFartherThanNeeded_IsReported_AsCanBeMovedCloser()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        ProjectAutoFix.Run(p);
        var row = PhotocellAudit.Run(p).First(r => r.DistanceMm != null);
        row.Photocell.PositionMm -= 600;
        var again = PhotocellAudit.Run(p).Single(r => r.Photocell == row.Photocell);
        Assert.Equal(PhotocellVerdict.AboveMinimum, again.Verdict);
        Assert.Contains(EquipmentCheck.Run(p, fix: false), c => c.Code == "PC_ABOVE_MIN" && !c.IsProblem);
    }
}
