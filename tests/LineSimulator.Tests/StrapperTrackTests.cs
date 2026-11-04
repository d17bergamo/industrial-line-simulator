using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Tests;

public class StrapperTrackTests
{
    private static RollerSection M(LineProject p, string id) => p.Sections.First(s => s.Id == id);

    [Fact]
    public void RollerDefaults_Are1250Pitch_268Diameter()
    {
        var s = new RollerSection();
        Assert.Equal(1250, s.RollerPitchMm);
        Assert.Equal(268, s.RollerDiameterMm);
        Assert.Equal(s.LengthMm, s.InteraxialLengthMm + 2 * RollerSection.StandardOverhangMm, 1e-6);
        foreach (var (_, create) in SampleProjects.All)
            foreach (var sec in create().Sections)
            {
                Assert.Equal(268, sec.RollerDiameterMm);
                Assert.InRange(sec.RollerPitchMm, 1250 * 0.8, 1250 * 1.2);
            }
    }

    [Fact]
    public void OldDefaults_AreConvertedKeepingPositionAndLength()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        foreach (var s in p.Sections) { s.RollerDiameterMm = 150; s.RollerPitchMm = 300; }
        var before = p.Sections.Select(s => (s.StartMm, s.LengthMm)).ToList();
        Assert.True(LineReflow.ApplyDefaultRollerGeometry(p));
        Assert.Equal(before, p.Sections.Select(s => (s.StartMm, s.LengthMm)).ToList());
        Assert.All(p.Sections, s => Assert.Equal(268, s.RollerDiameterMm));
    }

    [Fact]
    public void LastStrapper_GetsAtLeast2mOfTrack_ByExtendingItsModule()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        var m1 = M(p, "M1"); var m2 = M(p, "M2");
        var st = p.Strappers[0];
        st.PositionMm = m1.EndMm - 500;
        double m2Start = m2.StartMm, trf = p.Transfer.PositionMm;
        double added = StrapperTrack.EnsureMinimumTrack(p);
        Assert.Equal(1500, added, 1);
        Assert.Equal(2000, m1.EndMm - st.PositionMm, 1);
        Assert.Equal(m1.EndMm, m2.StartMm, 1);
        Assert.Equal(m2Start + 1500, m2.StartMm, 1);
        Assert.Equal(trf + 1500, p.Transfer.PositionMm, 1);
        Assert.True(LayoutRules.TransferModuleMatches(p));
        Assert.Equal(0, StrapperTrack.EnsureMinimumTrack(p));
    }

    [Fact]
    public void MovingTheLastStrapper_MovesTheDownstreamTrackWithIt()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        var m1 = M(p, "M1"); var m2 = M(p, "M2");
        var st = p.Strappers[0];
        double old = st.PositionMm, end = m1.EndMm, trf = p.Transfer.PositionMm, scale = p.Scales[0].StartMm, m1Start = m1.StartMm;
        st.PositionMm = old + 1000;
        Assert.True(StrapperTrack.FollowLastStrapper(p, st, old));
        Assert.Equal(old + 1000, st.PositionMm, 1);
        Assert.Equal(m1Start, m1.StartMm, 1);
        Assert.Equal(end + 1000, m1.EndMm, 1);
        Assert.Equal(m1.EndMm, m2.StartMm, 1);
        Assert.Equal(trf + 1000, p.Transfer.PositionMm, 1);
        Assert.Equal(scale + 1000, p.Scales[0].StartMm, 1);
        Assert.True(LayoutRules.TransferModuleMatches(p));
        // spostandola indietro la parte a valle torna al suo posto
        double now = st.PositionMm;
        st.PositionMm = now - 1000;
        Assert.True(StrapperTrack.FollowLastStrapper(p, st, now));
        Assert.Equal(end, m1.EndMm, 1);
        Assert.Equal(trf, p.Transfer.PositionMm, 1);
    }

    [Fact]
    public void MovingAStrapperThatIsNotTheLast_LeavesTheTrackAlone()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var first = p.Strappers.OrderBy(s => s.PositionMm).First();
        double old = first.PositionMm, end = M(p, "M1").EndMm;
        first.PositionMm = old - 500;
        Assert.False(StrapperTrack.FollowLastStrapper(p, first, old));
        Assert.Equal(end, M(p, "M1").EndMm, 1);
    }
}
