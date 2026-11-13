using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Tests;

public class StrapperTrackTests
{
    private static RollerSection M(LineProject p, string id) => p.Sections.First(s => s.Id == id);

    [Fact]
    public void RollerDefaults_Are1250Pitch_315Diameter()
    {
        var s = new RollerSection();
        Assert.Equal(1250, s.RollerPitchMm);
        Assert.Equal(315, s.RollerDiameterMm);
        Assert.Equal(s.LengthMm, s.InteraxialLengthMm + s.RollerPitchMm, 1e-6);
        foreach (var (_, create) in SampleProjects.All)
            foreach (var sec in create().Sections)
            {
                Assert.Equal(315, sec.RollerDiameterMm);
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
        Assert.All(p.Sections, s => Assert.Equal(315, s.RollerDiameterMm));
    }

    [Fact]
    public void LastStrapper_GetsAtLeastTwoRollersOfTrack_ByExtendingItsModule()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        var m1 = M(p, "M1"); var m2 = M(p, "M2");
        var st = p.Strappers[0];
        st.PositionMm = m1.EndMm - 500;
        double pitch = m1.RollerPitchMm;
        double m2Start = m2.StartMm, trf = p.Transfer.PositionMm;
        double added = StrapperTrack.EnsureMinimumTrack(p);
        Assert.Equal(2 * pitch - 500, added, 1);
        Assert.Equal(2 * pitch, m1.EndMm - st.PositionMm, 1);
        Assert.Equal(m1.EndMm, m2.StartMm, 1);
        Assert.Equal(m2Start + added, m2.StartMm, 1);
        Assert.Equal(trf + added, p.Transfer.PositionMm, 1);
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
        // prima legatrice ad almeno 1500 mm da FC0 (FC0 sullo zero): copre anche la regola dell'encoder (FC0 + 1400 mm)
        Assert.Equal(1500, p.Strappers[0].PositionMm, 6);
        Assert.Empty(EncoderRule.Problems(p).Where(m => m.Contains("troppo vicina")));
        Assert.False(StrapperTrack.ClampStrappersToTrackStart(p));
    }

    [Fact]
    public void FirstStrapper_IsAbout4mFromTheStartOfTheLine_InAllModels()
    {
        foreach (var (_, create) in SampleProjects.All)
        {
            var p = create();
            var fc0 = p.Photocells.First(x => x.IsEntryPhotocell);
            Assert.Equal(4000, p.Strappers.Min(s => s.PositionMm) - fc0.PositionMm, 6);
        }
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
    public void RetractableStop_AtCenterOrEnd_MovesOnlyWithTheManualCheckbox()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var fm = p.IntermediateStops[0];
        Assert.Equal(DevicePlacement.Center, fm.Placement);
        Assert.Equal(p.Transfer.PositionMm + p.Transfer.LengthMm / 2, fm.PositionMm, 1);
        fm.PositionMm += 700;                       // senza spunta torna al centro
        LayoutRules.SnapIntermediateStops(p);
        Assert.Equal(p.Transfer.PositionMm + p.Transfer.LengthMm / 2, fm.PositionMm, 1);
        fm.Placement = DevicePlacement.End;         // all'estremità (a monte, opposta al fermo fisso)
        LayoutRules.SnapIntermediateStops(p);
        Assert.Equal(p.Transfer.PositionMm, fm.PositionMm, 1);
        fm.ManualPosition = true;                   // con la spunta resta dove l'utente lo mette
        fm.PositionMm += 700;
        LayoutRules.SnapIntermediateStops(p);
        Assert.Equal(p.Transfer.PositionMm + 700, fm.PositionMm, 1);
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
        Assert.Equal(315, p.Sections[0].RollerDiameterMm);   // il diametro si imposta per modulo (dal padre con SetDiameterForAll)
        RollerGroups.SetDiameterForAll(p);
        Assert.All(p.Sections, x => Assert.Equal(300, x.RollerDiameterMm));
        Assert.Equal(1.0, p.Sections[1].HighSpeed);        // personalizzato: non segue il padre
    }

    [Fact]
    public void FirstPhotocell_StartsAtZeroTogetherWithTheTrack_AndDefaultWidthIs700()
    {
        foreach (var (_, create) in SampleProjects.All)
        {
            var p = create();
            Assert.Equal(p.Sections.Min(x => x.StartMm), p.Photocells.Single(x => x.IsEntryPhotocell).PositionMm, 6);
            Assert.All(p.Sections, x => Assert.Equal(700, x.WidthMm));
            Assert.Equal(p.Sections.Max(x => x.EndMm), p.Stopper.PositionMm, 6);
            Assert.All(p.Sections, x => Assert.Equal(0, x.RollerPitchMm % 50, 6));
        }
        var q = SampleProjects.CreateTwoStrappers12m();
        q.Photocells.Single(x => x.IsEntryPhotocell).PositionMm = 300;
        Assert.True(ProjectNormalizer.AlignEntryPhotocell(q));
        Assert.Equal(0, q.Photocells.Single(x => x.IsEntryPhotocell).PositionMm);
    }

    [Fact]
    public void DraggingTheLastModule_MovesTransferAndScalesAndStops_AndPitchStaysRound()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var last = LayoutRules.TransferModule(p)!;
        double trf = p.Transfer.PositionMm, sc = p.Scales[0].StartMm, fm = p.IntermediateStops[0].PositionMm;
        double dx = LineReflow.MoveSection(p, last, 1250);
        Assert.Equal(1250, dx, 6);
        Assert.Equal(trf + 1250, p.Transfer.PositionMm, 6);
        Assert.Equal(sc + 1250, p.Scales[0].StartMm, 6);
        Assert.Equal(fm + 1250, p.IntermediateStops[0].PositionMm, 6);
        Assert.Equal(last.EndMm, p.Stopper.PositionMm, 6);
        Assert.Empty(LineReflow.Discontinuities(p));
        Assert.All(p.Sections, x => Assert.Equal(0, x.RollerPitchMm % 50, 6));
    }

    [Fact]
    public void FamiliesOfPhotocellsScalesAndStrappers_PassPropertiesToNonCustomizedChildren()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        p.PhotocellDefaults.ResponseTimeMs = 40;
        p.ScaleDefaults.AcquisitionTimeS = 12;
        p.StrapperDefaults.StrappingTimeS = 9;
        p.Photocells[1].OverrideGroup = true;
        Assert.True(GroupInheritance.PropagateAll(p));
        Assert.Equal(40, p.Photocells[0].ResponseTimeMs);
        Assert.Equal(20, p.Photocells[1].ResponseTimeMs);
        Assert.All(p.Scales, x => Assert.Equal(12, x.AcquisitionTimeS));
        Assert.All(p.Strappers, x => Assert.Equal(9, x.StrappingTimeS));
        Assert.Contains("ResponseTimeMs", GroupInheritance.InheritedNames(p.PhotocellDefaults));
        // progetti vecchi: valori diversi dal padre restano, marcati come personalizzati
        var q = SampleProjects.CreateTwoStrappers12m();
        q.Strappers[0].StrappingTimeS = 7;
        Assert.True(GroupInheritance.MarkAllCustomized(q));
        Assert.True(q.Strappers[0].OverrideGroup);
        Assert.False(q.Strappers[1].OverrideGroup);
    }

    [Fact]
    public void EquipmentCheck_ListsProblemsWithoutFixing_ThenFixesThem()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Strappers[0].PositionMm = 200;
        p.Stopper.PositionMm += 300;
        var list = EquipmentCheck.Run(p, fix: false);
        Assert.Contains(list, i => i.Code == "STRAPPER_START" && i.IsProblem && !i.Fixed);
        Assert.Contains(list, i => i.Code == "STOPPER");
        Assert.Equal(200, p.Strappers[0].PositionMm);
        EquipmentCheck.Run(p);
        Assert.DoesNotContain(EquipmentCheck.Run(p, fix: false), i => i.IsProblem);
    }

    [Fact]
    public void MovingAModule_MovesTheMachinesAndPhotocellsInsideIt()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        var m2 = M(p, "M2");
        var inside = p.Photocells.Where(x => x.PositionMm >= m2.StartMm && x.PositionMm < m2.EndMm && !x.IsEntryPhotocell).ToDictionary(x => x, x => x.PositionMm);
        Assert.NotEmpty(inside);
        double dx = LineReflow.MoveSection(p, m2, 1250);
        Assert.Equal(1250, dx, 6);
        Assert.All(inside, kv => Assert.Equal(kv.Value + 1250, kv.Key.PositionMm, 6));
        // nella via di legatura (primo modulo) le legatrici seguono la via se la si sposta dentro
        var q = SampleProjects.CreateThreeStrappers24m();
        q.Sections.Add(new RollerSection { Id = "M0", StartMm = -2500, LengthMm = 2500 });
        var m1 = M(q, "M1");
        double before = q.Strappers[0].PositionMm;
        LineReflow.MoveSection(q, m1, 1250);
        Assert.Equal(before + 1250, q.Strappers[0].PositionMm, 6);
    }

    [Fact]
    public void EncoderAndReferencePhotocell_FollowTheFirstStrapperAtLeast1mAway()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        double gap0 = p.Strappers.Min(x => x.PositionMm) - p.StrappingEncoder.PositionMm;
        Assert.True(gap0 >= 1000);
        var first = p.Strappers.OrderBy(x => x.PositionMm).First();
        first.PositionMm = p.StrappingEncoder.PositionMm + 400;
        Assert.True(StrapperTrack.KeepEncoderBehindFirstStrapper(p));
        // regola unica: primo rullo a monte dell'ingombro (metà ingombro + passo), comunque almeno 1 m prima
        Assert.True(first.PositionMm - p.StrappingEncoder.PositionMm >= 1000 - 1e-6);
        Assert.Equal(EncoderRule.TargetMm(p)!.Value, p.StrappingEncoder.PositionMm, 6);
        var rif = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingReference);
        Assert.Equal(p.StrappingEncoder.PositionMm - 300, rif.PositionMm, 6);
    }

    [Fact]
    public void ExitPhotocell_IsHalfAPitchAfterTheLastRollerOfTheStrappingTrack()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var exit = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingExit);
        var m1 = M(p, "M1");
        Assert.Equal(m1.EndMm - m1.RollerPitchMm / 2 + m1.RollerPitchMm / 2, exit.PositionMm, 6);
        exit.PositionMm -= 700; exit.Action = PhotocellAction.Accumulation;
        Assert.True(LayoutRules.AlignStrappingExit(p));
        Assert.Equal(m1.EndMm, exit.PositionMm, 6);
        Assert.Equal(PhotocellAction.None, exit.Action);
    }

    [Fact]
    public void ExampleNames_MatchTheirSelectablePackages()
    {
        foreach (var (name, create) in SampleProjects.All)
        {
            var p = create();
            Assert.Equal(p.Infeed.TypeSequence.OrderBy(x => x), p.PackageTypes.Select(t => t.Id).OrderBy(x => x));
            foreach (var t in p.PackageTypes) Assert.Contains($"{t.LengthMm / 1000:0}", name);
            if (name.Contains("6/9/12 m")) Assert.DoesNotContain(p.PackageTypes, t => t.LengthMm > 12000);
        }
    }

    [Fact]
    public void LastStrapperPushedDownstream_ExtendsTheEndOfTheStrappingTrack()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var m1 = M(p, "M1");
        double end = m1.EndMm, trf = p.Transfer.PositionMm;
        var last = p.Strappers.OrderBy(x => x.PositionMm).Last();
        last.PositionMm = end + 3000;                       // spinta oltre la fine della via di legatura
        double added = StrapperTrack.EnsureMinimumTrack(p);
        Assert.True(added > 3000);
        Assert.True(m1.EndMm - last.PositionMm >= 2000);
        Assert.Equal(trf + (m1.EndMm - end), p.Transfer.PositionMm, 6);
        Assert.Equal(m1.EndMm, M(p, "M2").StartMm, 6);
    }

    [Fact]
    public void ReleaseDrive_FrontAccumulationAlwaysOwn_ReleaseParksOnlyIfShortestPackageFits()
    {
        // solo pacchi da 6 m: la legatura non impegna lo svincolo (5 m), il pacco non ci sta → inverter dedicato, senza parcheggio
        var p = SampleProjects.CreateTwoStrappers12m();
        p.PackageTypes.RemoveAll(t => t.LengthMm != 6000);
        p.Infeed.TypeSequence = p.PackageTypes.Select(t => t.Id).ToList();
        p.Simulation.AutoReleaseInverters = true;
        var rel = StrappingAreaRules.ReleaseModules(p).Single();
        var trf = LayoutRules.TransferModule(p)!;
        StrappingAreaRules.DecideReleaseInverters(p);
        Assert.Equal(trf.Id, trf.EffectiveZoneId);
        Assert.Equal(rel.Id, rel.EffectiveZoneId);
        Assert.Contains("inverter dedicato", StrappingAreaRules.DescribeDrive(p, rel));
        Assert.DoesNotContain("parcheggio", StrappingAreaRules.DescribeDrive(p, rel));
        // svincolo lungo abbastanza (6000 + 2000 + 500): parcheggio con inverter proprio
        Assert.False(StrappingAreaRules.CanPark(p, rel));
        double oldEnd = rel.EndMm;
        LineReflow.Fit(rel, rel.StartMm, rel.StartMm + 8750);
        LineReflow.AfterResize(p, rel, oldEnd);      // il resto della linea avanza: le vie restano unite
        Assert.True(StrappingAreaRules.CanPark(p, rel));
        StrappingAreaRules.DecideReleaseInverters(p);
        Assert.Equal(rel.Id, rel.EffectiveZoneId);
        Assert.Contains("parcheggio", StrappingAreaRules.DescribeDrive(p, rel));
        Assert.Equal(trf.Id, trf.EffectiveZoneId);
    }

    [Fact]
    public void TransferEntryPhotocell_FollowsTheUpstreamFootprint_AndStopsHaveTheirPhotocell()
    {
        var p = SampleProjects.CreateThreeStrappers24m();
        double d = TransferEntryRule.RequiredDistanceMm(p);
        Assert.Equal(1500, d);                                                  // Vmax²/(2a) + 0,2 × Vmax = 1,425 m → per eccesso a 100 mm, nessun minimo
        double foot = TransferEntryRule.UpstreamFootprintMm(p);
        Assert.True(foot <= p.Transfer.PositionMm);
        Assert.Equal(Math.Round(foot - d), TransferEntryRule.EntryPhotocellPositionMm(p));
        var fm = p.IntermediateStops.First();
        fm.Placement = DevicePlacement.Center;
        TransferEntryRule.EnsureStopPhotocells(p);
        var cmd = p.Photocells.Single(x => x.CommandsStopId == fm.Id && !x.ActsAsTransferStop);
        Assert.Equal(Math.Round(fm.PositionMm - d), cmd.PositionMm);
        fm.Placement = DevicePlacement.End;
        TransferEntryRule.EnsureStopPhotocells(p);
        Assert.DoesNotContain(p.Photocells, x => x.CommandsStopId == fm.Id && x.IsAutoGenerated && !x.ActsAsTransferStop);
        var tail = p.Photocells.Single(x => x.CommandsStopId == fm.Id + ":coda");           // fermo di coda: intestatura 100 mm dopo
        Assert.Equal(fm.PositionMm + 100, tail.PositionMm, 6);
    }
}
