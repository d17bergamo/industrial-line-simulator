using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Tests;

public class LayoutGeometryTests
{
    [Fact]
    public void InteraxialLength_IsPitchTimesRollersMinusOne()
    {
        var s = new RollerSection { RollerPitchMm = 200, RollerCount = 11, LengthMm = 2000 };
        Assert.Equal(2000, s.InteraxialLengthMm);
    }

    [Fact]
    public void ChangingPitchOrCount_UpdatesInteraxialAndPhysicalLength_KeepingOverhang()
    {
        var s = new RollerSection { RollerPitchMm = 200, RollerCount = 11, LengthMm = 2200 }; // sporgenza = metà passo
        Assert.Equal(100, s.EndOverhangMm);
        s.RollerPitchMm = 250;
        s.RecalculateLength();
        Assert.Equal(2500, s.InteraxialLengthMm);
        Assert.Equal(2750, s.LengthMm);
        s.RollerCount = 5;
        s.RecalculateLength();
        Assert.Equal(1000, s.InteraxialLengthMm);
        Assert.Equal(1250, s.LengthMm);
    }

    [Fact]
    public void InteraxialLength_IsNotSerializedButLengthRoundTrips()
    {
        var p = LegacySamples.CreateStandardLine();
        var json = LineSimulator.Infrastructure.ProjectSerializer.Serialize(p);
        Assert.DoesNotContain("interaxialLengthMm", json);
        var q = LineSimulator.Infrastructure.ProjectSerializer.Deserialize(json);
        Assert.Equal(p.Sections[0].LengthMm, q.Sections[0].LengthMm);
        Assert.Equal(p.Sections[0].EndOverhangMm, q.Sections[0].EndOverhangMm);
    }

    [Fact]
    public void EntryAndExit_AreTransformedByRotation()
    {
        var (x0, y0) = LayoutGeometry.ToGlobal(10, 2, 0, -1, 0);
        Assert.Equal(9, x0, 6); Assert.Equal(2, y0, 6);
        var (x1, y1) = LayoutGeometry.ToGlobal(10, 2, 180, -1, 0);
        Assert.Equal(11, x1, 6); Assert.Equal(2, y1, 6);
        var (x2, y2) = LayoutGeometry.ToGlobal(10, 2, 90, 1, 0);
        Assert.Equal(10, x2, 6); Assert.Equal(3, y2, 6);
    }

    [Fact]
    public void Describe_GivesCenterEntryExitOfRollerModulesAndDevices()
    {
        var p = LegacySamples.CreateStandardLine();
        var info = LayoutGeometry.Describe(p);
        var s = p.Sections.OrderBy(x => x.StartMm).First();
        var m = info.Single(i => i.Id == s.Id);
        Assert.Equal((s.StartMm + s.LengthMm / 2) / 1000.0, m.CenterXm, 6);
        Assert.Equal(s.StartMm / 1000.0, m.EntryXm, 6);
        Assert.Equal(s.EndMm / 1000.0, m.ExitXm, 6);
        var str = info.Single(i => i.Id == p.Strappers[0].Id);
        Assert.Equal(p.Strappers[0].PositionMm / 1000.0, str.CenterXm, 6);
        Assert.True(str.EntryXm < str.CenterXm && str.CenterXm < str.ExitXm);
    }

    [Fact]
    public void EntryReference_IsTheEntryPhotocellPosition()
    {
        var p = LegacySamples.CreateStandardLine();
        ProjectNormalizer.EnsureEntryPhotocell(p);
        var pc = p.Photocells.First(f => f.IsEntryPhotocell);
        Assert.Equal(pc.PositionMm / 1000.0, LayoutGeometry.EntryReferenceM(p), 6);
    }

    [Fact]
    public void Gaps_AreMeasuredBetweenExitAndNextEntry()
    {
        var p = LegacySamples.CreateFullProcessLine();
        var gaps = LayoutGeometry.Gaps(p);
        Assert.NotEmpty(gaps);
        var info = LayoutGeometry.Describe(p).ToDictionary(i => i.Id);
        foreach (var g in gaps)
        {
            Assert.Equal(info[g.ToId].EntryXm - info[g.FromId].ExitXm, g.ClearM, 6);
            Assert.Equal(info[g.ToId].CenterXm - info[g.FromId].CenterXm, g.CenterM, 6);
        }
    }
}
