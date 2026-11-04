using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>
/// Controllo completo dei quattro esempi: geometria, fotocellule, regole di layout, assenza di correzioni automatiche,
/// validazione senza errori né avvisi e simulazione pulita.
/// </summary>
public class ModelAuditTests
{
    public static IEnumerable<object[]> Models() => SampleProjects.All.Select(m => new object[] { m.Name });
    private static LineProject Model(string name) => SampleProjects.All.Single(m => m.Name == name).Factory();

    [Theory]
    [MemberData(nameof(Models))]
    public void Geometry_And_Photocells_AreConsistent(string name)
    {
        var p = Model(name);
        var s = p.Sections.OrderBy(x => x.StartMm).ToList();
        var m1 = s[0]; var m3 = s[^1];
        var fc0 = p.Photocells.Single(x => x.IsEntryPhotocell);
        Assert.Equal(m1.StartMm, fc0.PositionMm, 6);                                        // FC0 sullo zero della linea

        // Regola 1: fotocellula di riferimento ≥ FC0 + 100 mm, encoder 300 mm dopo, encoder ≥ 1 m prima della prima legatrice
        var refPc = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingReference);
        Assert.True(refPc.PositionMm >= fc0.PositionMm + 100 - 1e-6);
        Assert.Equal(refPc.PositionMm + 300, p.StrappingEncoder.PositionMm, 6);
        Assert.True(p.Strappers.Min(x => x.PositionMm) - p.StrappingEncoder.PositionMm >= 1000 - 1e-6);

        // Regola 2: uscita legatura alla fine della via di legatura, ad almeno 1 m dal trasferitore
        var exit = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingExit);
        Assert.Equal(m1.EndMm, exit.PositionMm, 6);
        Assert.True(p.Transfer.PositionMm - exit.PositionMm >= 1000 - 1e-6);

        // Regola 3: nessuna fotocellula di presenza sul fermo finale; resta il rallentamento, a 2 m dal fermo
        Assert.DoesNotContain(p.Photocells, x => x.PositionMm > p.Stopper.PositionMm - 1000);
        Assert.Contains(p.Photocells, x => x.Action == PhotocellAction.SlowDown && Math.Abs(p.Stopper.PositionMm - 2000 - x.PositionMm) < 1);
        // Fotocellule che rallentano o fermano ad almeno 2 m dal punto di arresto; nulla da unire o spostare prima della simulazione
        Assert.Empty(PhotocellRules.TooCloseToTarget(p));
        Assert.Empty(PhotocellRules.BeforeSimulation(Model(name)));

        // Fermo finale alla fine dell'ultimo modulo, a metà passo dall'ultimo rullo
        Assert.Equal(m3.EndMm, p.Stopper.PositionMm, 6);
        Assert.Equal(m3.RollerPitchMm / 2, p.Stopper.DistanceFromLastRollerMm, 6);
        Assert.Equal("Fermo finale", p.Stopper.Name);

        // Legatrici: prima a 4 m, a 2500 mm, stesso lato, due rulli dopo l'ultima, 3 m dal trasferitore; un KNOF ciascuna
        var st = p.Strappers.OrderBy(x => x.PositionMm).ToList();
        Assert.Equal(fc0.PositionMm + 4000, st[0].PositionMm, 6);
        for (int i = 1; i < st.Count; i++) Assert.Equal(2500, st[i].PositionMm - st[i - 1].PositionMm, 6);
        Assert.Single(st.Select(x => x.Side).Distinct());
        Assert.True(m1.EndMm - st[^1].PositionMm >= 2 * m1.RollerPitchMm - 1e-6);
        Assert.True(p.Transfer.PositionMm - st[^1].PositionMm >= 3000);
        Assert.All(LayoutRules.BundleClampPositions(p), k => Assert.InRange(k.PositionMm, m1.StartMm, m1.EndMm));

        // Svincolo 4 rulli; trasferitore = ultimo modulo; bilici affiancati sul trasferitore; fermo scomparente al centro
        Assert.Equal(4, s[1].RollerCount);
        Assert.Equal(m3.StartMm, p.Transfer.PositionMm, 6);
        Assert.Equal(m3.LengthMm, p.Transfer.LengthMm, 6);
        Assert.Equal(p.Transfer.LengthMm, p.Scales.Sum(b => b.LengthMm), 6);
        Assert.Equal(p.Transfer.PositionMm, p.Scales.Min(b => b.StartMm), 6);
        Assert.All(p.IntermediateStops, f => Assert.Equal(p.Transfer.PositionMm + p.Transfer.LengthMm / 2, f.PositionMm, 6));

        // Tutti i dispositivi dentro la linea
        double end = m3.EndMm;
        Assert.All(p.Photocells, x => Assert.InRange(x.PositionMm, m1.StartMm, end));

        // Nomi senza "intermedio" e senza "stopper"
        Assert.All(p.IntermediateStops, f => Assert.DoesNotContain("intermedio", f.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Rules_Check_Validation_NeedNoCorrections(string name)
    {
        var p = Model(name);
        Assert.Empty(LayoutRules.Enforce(p));
        var check = EquipmentCheck.Run(Model(name), fix: false);
        Assert.True(check.All(c => !c.IsProblem), string.Join(" | ", check.Where(c => c.IsProblem).Select(c => c.Code + ": " + c.Message)));
        var v = ProjectValidator.Validate(Model(name));
        Assert.False(v.HasErrors, string.Join(" | ", v.Errors.Select(x => x.Code + ": " + x.Message)));
        Assert.True(!v.Warnings.Any(), string.Join(" | ", v.Warnings.Select(x => x.Code + ": " + x.Message)));
        Assert.Empty(SimulationPreparation.Prepare(Model(name)).Changes);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Simulation_IsClean(string name)
    {
        var p = Model(name);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.Equal(p.Infeed.PackageCount, e.Packages.Count(k => k.State == PackageState.Exited));
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.All(e.Packages, k => Assert.NotNull(k.MeasuredWeightKg));
        Assert.Equal(0, e.Collisions);
        var bad = e.Events.Where(x => x.Severity >= EventSeverity.Warning).Select(x => $"{x.TimeS:0.0} {x.Type}: {x.Message}").Distinct().Take(15).ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void Rule1_ReferencePhotocellNeverBeforeFC0()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var refPc = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingReference);
        refPc.PositionMm = -500;                                    // spostata a mano prima di FC0
        Assert.Contains(EquipmentCheck.Run(p, fix: false), c => c.Code == "REF_PC_ENTRY");
        Assert.Contains(ProjectValidator.Validate(p).Warnings, x => x.Code == "REF_PC_ENTRY");
        var fixes = StrappingAreaRules.KeepReferenceAfterEntry(p);
        Assert.NotEmpty(fixes);
        Assert.Equal(100, refPc.PositionMm, 6);
        Assert.True(p.StrappingEncoder.PositionMm >= 400 - 1e-6);

        // Prima legatrice vicinissima all'inizio: encoder e fotocellula automatici non vanno a monte di FC0
        var q = SampleProjects.CreateOneStrapper12m();
        q.Strappers[0].PositionMm = 1000;
        SimulationPreparation.Prepare(q);
        Assert.True(q.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingReference).PositionMm >= 100 - 1e-6);
    }

    [Fact]
    public void Rule2_ExitTooCloseToTransfer_ReleaseModuleIsLengthenedOrInserted()
    {
        // Svincolo presente: allungato con rulli interi, la via di legatura e la fotocellula non si toccano
        var p = SampleProjects.CreateTwoStrappers12m();
        var m1 = p.Sections.Single(x => x.Id == "M1"); var m2 = p.Sections.Single(x => x.Id == "M2");
        double m1End = m1.EndMm, trf = p.Transfer.PositionMm;
        double oldEnd = m2.EndMm;
        m2.RollerPitchMm = 300;
        LineReflow.Fit(m2, m2.StartMm, m2.StartMm + 600);           // svincolo accorciato a 600 mm (2 rulli da 300)
        LineReflow.AfterResize(p, m2, oldEnd);
        Assert.True(StrappingAreaRules.ExitToTransferMm(p) < 1000);
        var fixes = StrappingAreaRules.KeepExitAwayFromTransfer(p);
        Assert.Contains(fixes, f => f.Contains("allungato", StringComparison.Ordinal));
        Assert.Equal(m1End, m1.EndMm, 6);
        Assert.True(StrappingAreaRules.ExitToTransferMm(p) >= 1000 - 1e-6);
        Assert.Equal(m2.EndMm, p.Transfer.PositionMm, 6);
        Assert.True(LayoutRules.TransferModuleMatches(p));

        // Senza svincolo: ne viene inserito uno
        var q = SampleProjects.CreateTwoStrappers12m();
        var r2 = q.Sections.Single(x => x.Id == "M2");
        q.Sections.Remove(r2);
        LineReflow.CloseGap(q, r2.StartMm, r2.EndMm);
        double qm1End = q.Sections.Single(x => x.Id == "M1").EndMm;
        Assert.Equal(qm1End, q.Transfer.PositionMm, 6);
        Assert.Contains(EquipmentCheck.Run(q, fix: false), c => c.Code == "EXIT_TRF_DIST");
        var added = StrappingAreaRules.KeepExitAwayFromTransfer(q);
        Assert.Contains(added, f => f.Contains("Inserito", StringComparison.Ordinal));
        Assert.Equal(3, q.Sections.Count);
        Assert.True(q.Transfer.PositionMm - qm1End >= 1000 - 1e-6);
        Assert.Equal(qm1End, q.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingExit).PositionMm, 6);
        Assert.Empty(LineReflow.Discontinuities(q));
    }

    [Fact]
    public void SlowOrStopPhotocells_AreMovedTo2mFromTheirTarget()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var slow = p.Photocells.Single(x => x.Action == PhotocellAction.SlowDown);
        slow.PositionMm = p.Stopper.PositionMm - 1200;                       // troppo vicina al fermo finale
        var trf = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.TransferStop);
        trf.PositionMm = p.Transfer.PositionMm - 1500;                       // troppo vicina al trasferitore
        Assert.Equal(2, PhotocellRules.TooCloseToTarget(p).Count());
        Assert.Contains(ProjectValidator.Validate(p).Warnings, x => x.Code == "PC_TARGET_DIST");
        Assert.Contains(EquipmentCheck.Run(p, fix: false), c => c.Code == "PC_TARGET_DIST");
        var fixes = PhotocellRules.BeforeSimulation(p);
        Assert.Equal(p.Stopper.PositionMm - 2000, slow.PositionMm, 6);
        Assert.Equal(p.Transfer.PositionMm - 2000, trf.PositionMm, 6);
        Assert.Empty(PhotocellRules.TooCloseToTarget(p));
        Assert.Equal(2, fixes.Count);
    }

    [Fact]
    public void ExitAndTransferStop_WhenClose_AreMergedIntoOnePhotocell()
    {
        // Margine di sicurezza più grande: l'arresto davanti al trasferitore va a 4,5 m, 500 mm dopo l'uscita legatura (a 5 m)
        var p = SampleProjects.CreateTwoStrappers12m();
        p.Transfer.StopPhotocellMarginMm = 3000;
        var exit = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingExit);
        var stop = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.TransferStop);
        stop.PositionMm = p.Transfer.PositionMm - ProjectNormalizer.TransferStopDistanceMm(p);
        Assert.True(Math.Abs(exit.PositionMm - stop.PositionMm) <= 1000);
        Assert.NotNull(PhotocellRules.MergeCandidate(p));
        var fixes = PhotocellRules.MergeExitWithTransferStop(p);
        Assert.Single(fixes);
        Assert.DoesNotContain(p.Photocells, x => x.Purpose == PhotocellPurpose.TransferStop);
        Assert.True(exit.AlsoTransferStop && exit.ActsAsTransferStop);
        Assert.Equal(p.Sections.Single(x => x.Id == "M1").EndMm, exit.PositionMm, 6);   // resta a fine via di legatura
        // Nessuna nuova fotocellula di arresto aggiunta in automatico, e la simulazione funziona
        Assert.Empty(ProjectNormalizer.EnsureProcessSensors(p).Where(a => a.Contains("arresto", StringComparison.Ordinal)));
        p.Infeed.PackageCount = 3;
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.Equal(0, e.Collisions);
    }
}
