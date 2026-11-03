using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Tests;

public class StrapEditingTests
{
    private static PackageType Pack(params double[] straps) => new() { Id = "P", LengthMm = 12000, StrapPositionsMm = straps.ToList() };

    [Theory]
    [InlineData(1549, 1500)]
    [InlineData(1550, 1600)]
    [InlineData(6000, 6000)]
    [InlineData(10490, 10500)]
    public void Snap_GoesToMultiplesOf100(double input, double expected) => Assert.Equal(expected, StrapEditing.Snap(input));

    [Fact]
    public void SnapAll_SortsDedupesAndKeepsInsidePackage()
    {
        var p = Pack(10540, 1510, 1490, 0, 12000, 6000);
        StrapEditing.SnapAll(p);
        Assert.Equal(new[] { 1500.0, 6000, 10500 }, p.StrapPositionsMm);
    }

    [Fact]
    public void TryAdd_PlacesInLargestGap_OnMultiplesOf100_RespectingMinimums()
    {
        var p = Pack(1500, 10500);
        Assert.True(StrapEditing.TryAdd(p, out var a));
        Assert.Equal(6000, a);
        Assert.Equal(3, p.StrapPositionsMm.Count);
        Assert.All(p.StrapPositionsMm, v => Assert.Equal(0, v % 100));
        for (int i = 1; i < p.StrapPositionsMm.Count; i++)
            Assert.True(p.StrapPositionsMm[i] - p.StrapPositionsMm[i - 1] >= p.MinDistanceBetweenStrapsMm);
    }

    [Fact]
    public void TryAdd_OnEmpty_UsesTheMiddle()
    {
        var p = Pack();
        Assert.True(StrapEditing.TryAdd(p, out var a));
        Assert.Equal(6000, a);
    }

    [Fact]
    public void TryAdd_FailsWhenThereIsNoRoom()
    {
        var p = new PackageType { Id = "P", LengthMm = 1000, MinStrapDistanceFromEndsMm = 300, MinDistanceBetweenStrapsMm = 500, StrapPositionsMm = new() { 400, 900 - 200 } };
        Assert.False(StrapEditing.TryAdd(p, out _));
        Assert.Equal(2, p.StrapPositionsMm.Count);
    }

    [Fact]
    public void Remove_RemovesByIndexInSortedOrder()
    {
        var p = Pack(6000, 1500, 10500);
        Assert.True(StrapEditing.Remove(p, 1));
        Assert.Equal(new[] { 1500.0, 10500 }, p.StrapPositionsMm);
        Assert.False(StrapEditing.Remove(p, 5));
    }
}

public class StopperAlignmentTests
{
    [Fact]
    public void Stopper_IsAfterTheLastRoller_AtTheSetDistance()
    {
        var p = SampleProjects.CreateStandardLine();
        var last = p.Sections.OrderBy(s => s.EndMm).Last();
        double lastRoller = last.EndMm - last.EndOverhangMm;
        p.Stopper.DistanceFromLastRollerMm = 40;
        Assert.True(ProjectNormalizer.AlignStopperToLastRoller(p));
        Assert.Equal(lastRoller + 40, p.Stopper.PositionMm, 6);
        Assert.False(ProjectNormalizer.AlignStopperToLastRoller(p));   // idempotente
    }

    [Fact]
    public void Stopper_FollowsTheLastRoller_WhenTheLastSectionMoves()
    {
        var p = SampleProjects.CreateStandardLine();
        ProjectNormalizer.AlignStopperToLastRoller(p);
        double before = p.Stopper.PositionMm;
        p.Sections.OrderBy(s => s.EndMm).Last().StartMm += 1000;
        ProjectNormalizer.AlignStopperToLastRoller(p);
        Assert.Equal(before + 1000, p.Stopper.PositionMm, 6);
    }

    [Fact]
    public void Stopper_BeyondTheLastSection_StillBelongsToTheLastZone()
    {
        var p = SampleProjects.CreateStandardLine();
        var last = p.Sections.OrderBy(s => s.EndMm).Last();
        p.Stopper.DistanceFromLastRollerMm = last.EndOverhangMm + 500;   // oltre la fine della via a rulli
        ProjectNormalizer.AlignStopperToLastRoller(p);
        var topo = new LineTopology(p);
        Assert.NotNull(topo.StopperZone);
        Assert.Equal(last.Id, topo.StopperZone!.Sections.Last().Id);
    }
}
