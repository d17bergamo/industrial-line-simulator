using System.Reflection;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>
/// Svincolo da 4 rulli, prima legatrice a 4 m (pacco legato con la coda ancora fuori linea), decisione automatica degli
/// inverter di legatura e svincolo, macchine sullo stesso lato a 2 m, bilici "bancale" con posizione automatica.
/// </summary>
public class StrappingAreaTests
{
    private static string Diag(SimulationEngine e) =>
        $"{e.FinishReason} t={e.Time:0.0}\n" + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(30));

    public static IEnumerable<object[]> Models() => SampleProjects.All.Select(m => new object[] { m.Name });
    private static LineProject Model(string name) => SampleProjects.All.Single(m => m.Name == name).Factory();

    [Theory]
    [MemberData(nameof(Models))]
    public void Models_HaveStrapping_Release4Rollers_TransferModules(string name)
    {
        var p = Model(name);
        var s = p.Sections.OrderBy(x => x.StartMm).ToList();
        Assert.Equal(3, s.Count);
        Assert.Equal(4, s[1].RollerCount);                                 // svincolo da 4 rulli
        Assert.Equal(s[0].EndMm, s[1].StartMm, 6);
        Assert.Equal(s[1].EndMm, s[2].StartMm, 6);
        Assert.Equal(p.Transfer.PositionMm, s[2].StartMm, 6);
        Assert.Equal(4000, p.Strappers.Min(x => x.PositionMm), 6);          // prima legatrice a 4 m dall'inizio
        double last = p.Strappers.Max(x => x.PositionMm);
        Assert.True(s[0].EndMm - last >= 2 * s[0].RollerPitchMm - 1e-6);   // almeno due rulli dopo l'ultima legatrice
        Assert.All(p.Strappers, x => Assert.Equal(p.Strappers[0].Side, x.Side));
        // fotocellula di uscita legatura a fine via di legatura
        Assert.Equal(s[0].EndMm, p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingExit).PositionMm, 6);
        // nessuna correzione all'avvio: la decisione sugli inverter è già presa nel modello
        Assert.Empty(StrappingAreaRules.DecideReleaseInverters(p));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Models_StrapPackagesWithTheTailStillOutsideTheLine(string name)
    {
        var p = Model(name);
        var e = new SimulationEngine(p);
        Assert.Empty(e.Preparation.Changes);
        bool tailOutside = false;
        while (e.Step())
            if (e.Packages.Any(k => k.State == PackageState.Strapping && k.TailM < -1e-6)) tailOutside = true;
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.True(tailOutside, "nessun pacco legato con la coda fuori dalla linea");
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.Equal(p.Infeed.PackageCount, e.Packages.Count(k => k.State == PackageState.Exited));
        Assert.Equal(0, e.Collisions);
    }

    [Fact]
    public void ReleaseModule_JoinedWhenStrappingNeedsIt_SeparateWhenTheStrappingTrackIsLongEnough()
    {
        // Modello: la legatura di coda del pacco più lungo porta la testa oltre la via di legatura → svincolo sotto lo stesso inverter
        var p = SampleProjects.CreateTwoStrappers12m();
        var topo = new LineTopology(p);
        Assert.Same(topo.Zone("M1"), topo.Zone("M2"));
        Assert.True(StrappingAreaRules.RequiredStrappingHeadMaxMm(p) > p.Sections.Single(s => s.Id == "M1").EndMm);

        // Solo pacchi da 6 m nella simulazione (non basta toglierli dall'ingresso: si guardano tutte le tipologie)
        var q = SampleProjects.CreateTwoStrappers12m();
        q.Infeed.TypeSequence = new List<string> { "P6 (Wire)" };
        Assert.Empty(StrappingAreaRules.DecideReleaseInverters(q));
        q.PackageTypes.RemoveAll(t => t.Id != "P6 (Wire)");
        var changes = StrappingAreaRules.DecideReleaseInverters(q);
        Assert.Contains(changes, c => c.Contains("inverter proprio", StringComparison.Ordinal));
        var tq = new LineTopology(q);
        Assert.NotSame(tq.Zone("M1"), tq.Zone("M2"));
        q.Infeed.PackageCount = 4;
        var e = new SimulationEngine(q);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));

        // Tornando ai pacchi da 12 m il software li riunisce (anche dalla preparazione all'avvio)
        q.PackageTypes.Add(SampleProjects.Package(12));
        q.Infeed.TypeSequence = new List<string> { "P12 (Wire)" };
        var r = SimulationPreparation.Prepare(q);
        Assert.Contains(r.Changes, c => c.Contains("stesso inverter", StringComparison.Ordinal));
        var tq2 = new LineTopology(q);
        Assert.Same(tq2.Zone("M1"), tq2.Zone("M2"));
    }

    [Fact]
    public void JoinedInverter_NextPackageStartsAfterExitPhotocell_PreviousMayStopOnTheReleaseModule()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Infeed.TypeSequence = new List<string> { "P12 (Wire)" };
        p.Infeed.PackageCount = 3;
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.Equal(0, e.Collisions);
        Assert.DoesNotContain(e.Events, x => x.Type == SimEventType.ZoneOverfilled);
    }

    [Fact]
    public void SameSideMachines_AtLeast2mApart_OthersArePushed_OppositeSidesFree()
    {
        var p = SampleProjects.CreateThreeStrappers24m();           // 4000, 6500, 9000 sullo stesso lato
        var l1 = p.Strappers[0]; var l2 = p.Strappers[1]; var l3 = p.Strappers[2];
        l1.PositionMm = 6000;                                       // L1 si avvicina a L2: L2 e L3 vengono spinte
        StrappingAreaRules.SpreadStrappers(p, l1);
        Assert.Equal(6000, l1.PositionMm, 6);
        Assert.Equal(8000, l2.PositionMm, 6);
        Assert.Equal(10000, l3.PositionMm, 6);
        Assert.Empty(StrappingAreaRules.SameSideConflicts(p));

        l3.PositionMm = 8500;                                       // L3 arretra verso L2: L2 e L1 arretrano
        StrappingAreaRules.SpreadStrappers(p, l3);
        Assert.Equal(8500, l3.PositionMm, 6);
        Assert.Equal(6500, l2.PositionMm, 6);
        Assert.Equal(4500, l1.PositionMm, 6);

        // Lati opposti: nessun vincolo
        l2.Side = l1.Side == LateralSide.Left ? LateralSide.Right : LateralSide.Left;
        l2.PositionMm = l1.PositionMm + 300;
        Assert.Empty(StrappingAreaRules.SpreadStrappers(p, l2));
        Assert.Empty(StrappingAreaRules.SameSideConflicts(p));
        Assert.DoesNotContain(ProjectValidator.Validate(p).Errors, x => x.Code == "STR_SIDE_DIST");

        // Stesso lato a meno di 2 m: errore di validazione
        l2.Side = l1.Side;
        Assert.Contains(ProjectValidator.Validate(p).Errors, x => x.Code == "STR_SIDE_DIST");
    }

    [Fact]
    public void Scale_IsJustAWeighingPallet_WithAutomaticLength()
    {
        var hidden = typeof(WeighingScaleConfig).GetCustomAttribute<UiHiddenPropertiesAttribute>()!.Names;
        foreach (var n in new[] { "ResolutionKg", "CapacityKg", "StartConsent", "StopCondition", "ConfirmDelayS", "ConfirmTimeoutS", "PositionToleranceMm", "Model", "StartMm", "LengthMm" })
            Assert.Contains(n, hidden);
        var visible = typeof(WeighingScaleConfig).GetProperties()
            .Where(x => x.GetCustomAttribute<UiPropertyAttribute>() != null && !hidden.Contains(x.Name))
            .Select(x => x.GetCustomAttribute<UiPropertyAttribute>()!.Category).Distinct().ToList();
        Assert.DoesNotContain("Geometria", visible);
        Assert.DoesNotContain("Interblocchi", visible);
        Assert.DoesNotContain("Pesatura", visible);
        Assert.DoesNotContain("Posizionamento", visible);

        var p = SampleProjects.CreateTwoStrappers18m();             // due bilici: metà trasferitore ciascuno
        Assert.All(p.Scales, s => Assert.Equal(p.Transfer.LengthMm / 2, s.LengthMm, 6));
        p.Scales.Add(new WeighingScaleConfig { Id = "B3", Placement = DevicePlacement.End });
        Assert.True(StrappingAreaRules.LayoutScales(p));
        Assert.All(p.Scales, s => Assert.Equal(p.Transfer.LengthMm / 3, s.LengthMm, 6));
        Assert.Equal(p.Transfer.PositionMm, p.Scales.Min(s => s.StartMm), 6);
        Assert.Equal(p.Transfer.EndMm, p.Scales.Max(s => s.EndMm), 6);
        var manual = p.Scales[0]; manual.ManualPosition = true; manual.StartMm += 300;
        StrappingAreaRules.LayoutScales(p);
        Assert.True(manual.StartMm > p.Transfer.PositionMm);       // con la spunta resta dove l'utente l'ha messo
    }
}
