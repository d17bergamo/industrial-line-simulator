using System.Reflection;
using LineSimulator.Core;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

public class EssentialPropertiesTests
{
    [Fact]
    public void EveryEssentialName_IsAnEditablePropertyOfItsType()
    {
        var types = typeof(LineProject).Assembly.GetTypes().Where(t => t.IsClass && !t.IsNested).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var (typeName, names) in EssentialProperties.All)
        {
            Assert.True(types.ContainsKey(typeName), typeName);
            var shown = types[typeName].GetProperties().Where(p => p.GetCustomAttribute<UiPropertyAttribute>() != null).Select(p => p.Name).ToHashSet();
            foreach (var n in names) Assert.True(shown.Contains(n), $"{typeName}.{n}");
            Assert.True(shown.Count(n => !EssentialProperties.IsAdvanced(typeName, n)) <= 7, $"{typeName}: scheda breve troppo lunga");
        }
    }

    [Fact]
    public void SimpleMachines_ShowFewFields_AndOverrideIsAlwaysVisible()
    {
        Assert.True(EssentialProperties.IsAdvanced("StrapperConfig", "ConsentTimeoutS"));
        Assert.False(EssentialProperties.IsAdvanced("StrapperConfig", "PositionMm"));
        Assert.False(EssentialProperties.IsAdvanced("PhotocellConfig", "OverrideGroup"));
        Assert.False(EssentialProperties.IsAdvanced("UnknownType", "Anything"));
    }

    [Fact]
    public void EmptyLayout_HasNoStrappers_AndSimulates()
    {
        var p = SampleProjects.CreateEmpty();
        Assert.Empty(p.Strappers);
        Assert.NotEmpty(p.PackageTypes);
        p.Infeed.PackageCount = 3;
        var e = new LineSimulator.Engine.SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally);
        Assert.True(p.Infeed.Continuous);                                        // in app la simulazione è continua di default
        Assert.Equal(10, new WeighingScaleConfig().AcquisitionTimeS);           // bilico: solo il tempo di pesata, 10 s
    }

    [Fact]
    public void InLineScale_WeighsOnTheRollers_BeforeTheTransferPicks()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        foreach (var sc in p.Scales) sc.Mounting = ScaleMounting.InLine;
        p.Infeed.PackageCount = 3;
        var e = new LineSimulator.Engine.SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally);
        Assert.Contains(e.Events, ev => ev.Message.Contains("bilico integrato", StringComparison.Ordinal));
        Assert.Empty(e.Scales);                                                  // nessun bilico bancale
    }

    [Fact]
    public void ManualPhotocell_RoleDecidedByTheModel_AndRollersShowOnlyLengthAndCount()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        foreach (var x in p.Photocells.Where(x => x.Action == PhotocellAction.SlowDown)) x.Action = PhotocellAction.None;
        var trf = LayoutRules.TransferModule(p)!;
        var pc = new PhotocellConfig { Id = "FCX", Name = "Nuova fotocellula", PositionMm = trf.StartMm + 1000 };
        p.Photocells.Add(pc);
        PhotocellRoles.Decide(p, pc);
        Assert.Equal(PhotocellAction.SlowDown, pc.Action);
        var pc2 = new PhotocellConfig { Id = "FCY", Name = "Nuova fotocellula", PositionMm = 3000 };
        p.Photocells.Add(pc2);
        PhotocellRoles.Decide(p, pc2);
        Assert.Equal(PhotocellAction.None, pc2.Action);
        Assert.Equal(PhotocellDetectionMode.Presence, pc2.DetectionMode);
        Assert.True(EssentialProperties.IsHidden("RollerSection", "RollerPitchMm"));
        Assert.True(EssentialProperties.IsHidden("RollerSection", "OverrideGroup"));
        Assert.False(EssentialProperties.IsHidden("RollerSection", "LengthMm"));
        Assert.True(EssentialProperties.IsHidden("StopperConfig", "MaxImpactSpeed"));
        Assert.True(EssentialProperties.IsHidden("PhotocellConfig", "Action"));
        Assert.True(new AccumulationTransferConfig().IsEnabled);
    }

    [Fact]
    public void Defaults_WireReload6_5_Diameter315_AndDynamicDescription()
    {
        Assert.Equal(6.5, new StrapperConfig().ArmingTimeS);
        Assert.Equal(315, new RollerSection().RollerDiameterMm);
        var p = SampleProjects.CreateTwoStrappers12m();
        ProjectDescription.Refresh(p);
        Assert.Equal("2 legatrici - pacchi 6/9/12 m", p.Name);
        p.Strappers.RemoveAt(1);
        ProjectDescription.Refresh(p);
        Assert.Equal("1 legatrice - pacchi 6/9/12 m", p.Name);
        Assert.StartsWith("1 legatrice · pacchi 6/9/12 m", p.Description);
        p.Name = "Impianto cliente";
        ProjectDescription.Refresh(p);
        Assert.Equal("Impianto cliente", p.Name);                 // un nome scelto dall'utente non si tocca
    }

    [Fact]
    public void SteadyState_IgnoresTheFillingAndEmptyingOfTheLine()
    {
        var c = new List<double> { 200, 150, 101, 100, 99, 100, 101, 100, 99, 100, 100, 100, 40 };
        var (s, e) = LineSimulator.Engine.ResultsCalculator.SteadyWindow(c, 1);
        Assert.True(s >= 1);
        Assert.True(e <= c.Count - 1);
        Assert.InRange(c.Skip(s).Take(e - s).Average(), 99, 101);
    }

    [Fact]
    public void PrintableCyclogram_IsAnA3PdfForEveryExample()
    {
        string? dir = Environment.GetEnvironmentVariable("PDF_PREVIEW_DIR");
        int n = 0;
        foreach (var (_, create) in SampleProjects.All)
        {
            var pdf = LineSimulator.Infrastructure.CyclogramPrintPdf.Build(create());
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, $"ciclogramma-stampa-{++n}.pdf"), pdf); }
            var text = System.Text.Encoding.Latin1.GetString(pdf);
            Assert.StartsWith("%PDF", text);
            Assert.Contains("/MediaBox [0 0 1191 842]", text);
            Assert.Contains("Ciclogramma di un ciclo a regime", text);
            Assert.Contains("VIE A RULLI", text);
        }
    }
}

/// <summary>Pagina Pacchi (tempo e produttività per pacco), esempi con la regola della via di legatura, fotocellule.</summary>
public class PackagePageAndPhotocellTests
{
    [Fact]
    public void Examples_StrappingTrack_EndsTwoRollersAfterLastStrapper()
    {
        foreach (var p in new[] { SampleProjects.CreateOneStrapper12m(), SampleProjects.CreateTwoStrappers12m(), SampleProjects.CreateTwoStrappers18m(), SampleProjects.CreateThreeStrappers24m() })
        {
            var last = StrapperTrack.LastStrapper(p)!;
            var sec = StrapperTrack.SectionAt(p, last.PositionMm)!;
            double after = sec.EndMm - last.PositionMm;
            Assert.InRange(after, StrapperTrack.MinRollersAfterLastStrapper * sec.RollerPitchMm - 1, StrapperTrack.MinRollersAfterLastStrapper * sec.RollerPitchMm + sec.RollerPitchMm);
        }
    }

    [Fact]
    public void StopperPhotocell_FollowsTheTransferArea()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var pc = ProjectNormalizer.StopperPhotocell(p)!;
        double d = p.Stopper.PositionMm - pc.PositionMm;
        var trfMod = LayoutRules.TransferModule(p)!;
        LineReflow.MoveSection(p, trfMod, 1250);
        Assert.Equal(d, p.Stopper.PositionMm - pc.PositionMm, 3);
        // trasferitore più lungo: il fermo va avanti e la fotocellula con lui
        p.Transfer.LengthMm += 2500;
        LayoutRules.AlignTransferModule(p);
        Assert.Equal(d, p.Stopper.PositionMm - pc.PositionMm, 3);
    }

    [Fact]
    public void Photocells_NeverOutsideRollers()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var pc = p.Photocells.First(x => !x.IsEntryPhotocell);
        pc.PositionMm = p.Sections.Max(s => s.EndMm) + 3000;
        Assert.True(ProjectNormalizer.ClampPhotocellsToRollers(p));
        Assert.Equal(p.Sections.Max(s => s.EndMm), pc.PositionMm, 3);
        pc.PositionMm = -500;
        ProjectAutoFix.Run(p);
        Assert.True(pc.PositionMm >= p.Sections.Min(s => s.StartMm) - 1e-6);
    }

    [Fact]
    public void PackageProductivity_PerType_AndPerTypeCyclogram()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        foreach (var t in p.PackageTypes)
        {
            var r = PackageProductivity.Evaluate(p, t.Id);
            Assert.Null(r.Problem);
            Assert.True(r.CycleTimeS > 0 && r.TonnesPerHour > 0, $"{t.Id}: {r.CycleTimeS} s {r.TonnesPerHour} t/h");
        }
        var pdf = CyclogramPrintPdf.Build(p, null, p.PackageTypes[^1].Id);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
