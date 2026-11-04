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
        Assert.Equal(s.LengthMm, s.InteraxialLengthMm + s.RollerPitchMm, 1e-6);
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
    public void StrapperInsideTheTrack_DoesNotChangeTheModule()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        var m1 = M(p, "M1");
        double end = m1.EndMm, trf = p.Transfer.PositionMm;
        p.Strappers[0].PositionMm += 1000;           // resta a più di 2 m dalla fine: la via non si tocca
        Assert.Equal(0, StrapperTrack.EnsureMinimumTrack(p));
        Assert.Equal(end, m1.EndMm, 6);
        Assert.Equal(trf, p.Transfer.PositionMm, 6);
    }

    [Fact]
    public void Strapper_CannotGetCloserThan1mToTheTrackStart()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Strappers[0].PositionMm = 200;
        Assert.True(StrapperTrack.ClampStrappersToTrackStart(p));
        Assert.Equal(1000, p.Strappers[0].PositionMm, 6);
        Assert.False(StrapperTrack.ClampStrappersToTrackStart(p));
    }

    [Fact]
    public void FirstStrapper_IsAtLeast5mFromTheFirstPhotocell_AndAsCloseAsTheLongestPackageAllows()
    {
        foreach (var (_, create) in SampleProjects.All)
        {
            var p = create();
            var fc0 = p.Photocells.First(x => x.IsEntryPhotocell);
            double d = p.Strappers.Min(s => s.PositionMm) - fc0.PositionMm;
            Assert.True(d >= 5000 - 1e-6);
            double needed = p.PackageTypes.Max(t => t.LengthMm) - 500 + 100 - (p.Strappers.Count - 1) * 2500 - fc0.PositionMm;
            Assert.True(d <= Math.Max(5000, needed) + 100);
        }
        Assert.Equal(5300, SampleProjects.FirstStrapperPositionMm(1, 5));
    }

    [Fact]
    public void ModelsWith6_9_12_HaveASingleScale_AndStartupModelIsTwoStrappers()
    {
        Assert.Single(SampleProjects.CreateOneStrapper12m().Scales);
        Assert.Single(SampleProjects.CreateTwoStrappers12m().Scales);
        Assert.Equal(2, SampleProjects.CreateTwoStrappers18m().Scales.Count);
        var std = SampleProjects.CreateStandardLine();
        Assert.Equal(2, std.Strappers.Count);
        Assert.Equal(new[] { "P6 (Wire)", "P9 (Wire)", "P12 (Wire)" }, std.PackageTypes.Where(t => std.Infeed.TypeSequence.Contains(t.Id)).Select(t => t.Id).OrderBy(x => x.Length).ThenBy(x => x));
    }

    [Fact]
    public void StopRule_BetweenTwoScales_OrCenterOrUpstreamEndOfASingleOne()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var b1 = p.Scales[0]; var b2 = p.Scales[1];
        var fm = p.IntermediateStops[0];
        Assert.Equal((b1.EndMm + b2.StartMm) / 2, fm.PositionMm, 1);
        fm.PositionMm = b1.StartMm + 100;
        LayoutRules.SnapIntermediateStops(p);
        Assert.Equal((b1.EndMm + b2.StartMm) / 2, fm.PositionMm, 1);

        var q = SampleProjects.CreateTwoStrappers12m();
        q.IntermediateStops.Add(new IntermediateStopConfig { Id = "FM1", PositionMm = q.Scales[0].StartMm + 200 });
        LayoutRules.SnapIntermediateStops(q);
        Assert.Equal(q.Scales[0].StartMm, q.IntermediateStops[0].PositionMm, 1);       // estremo opposto al fermo fisso
        q.IntermediateStops[0].PositionMm = q.Scales[0].StartMm + q.Scales[0].LengthMm * 0.45;
        LayoutRules.SnapIntermediateStops(q);
        Assert.Equal(q.Scales[0].StartMm + q.Scales[0].LengthMm / 2, q.IntermediateStops[0].PositionMm, 1);
    }

    [Fact]
    public void PackageIds_FollowLengthAndBinding_AndStrapsAre500FromEnds()
    {
        var t = SampleProjects.Package(12, BindingType.SteelStrap);
        Assert.Equal("P12 (Still Strapp)", t.Id);
        Assert.Equal(500, t.StrapPositionsMm.First());
        Assert.Equal(12000 - 500, t.StrapPositionsMm.Last());
        var p = SampleProjects.CreateTwoStrappers12m();
        var pk = p.PackageTypes.First(x => x.Id == "P12 (Wire)");
        pk.BindingType = BindingType.SteelStrap;
        Assert.True(PackageNaming.Refresh(p, pk));
        Assert.Equal("P12 (Still Strapp)", pk.Id);
        Assert.Contains("P12 (Still Strapp)", p.Infeed.TypeSequence);
    }

    [Fact]
    public void Group_PropagatesToNonCustomizedModules_AndDefaultsAreAsRequested()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var g = p.SectionDefaults;
        Assert.Equal((0.3, 1.0, 1.5, 1.0, 1.0), (g.LowSpeed, g.HighSpeed, g.MaxSpeed, g.Acceleration, g.Deceleration));
        p.Sections[1].OverrideGroup = true;
        g.HighSpeed = 0.8; g.RollerDiameterMm = 300;
        Assert.True(RollerGroups.Propagate(p, applyPitch: false));
        Assert.Equal(0.8, p.Sections[0].HighSpeed);
        Assert.Equal(300, p.Sections[0].RollerDiameterMm);
        Assert.Equal(1.0, p.Sections[1].HighSpeed);        // personalizzato: non segue il padre
    }
}
