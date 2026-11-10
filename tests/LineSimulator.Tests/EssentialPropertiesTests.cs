using System.Reflection;
using LineSimulator.Core;
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
}
