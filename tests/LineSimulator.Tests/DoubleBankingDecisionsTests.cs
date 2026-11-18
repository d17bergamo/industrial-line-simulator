using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// Decisioni dell'utente del 2026-11-18 sul double banking e sull'uscita della via di legatura:
/// 1. il pacco senza posto libero si ferma alla fotocellula davanti al trasferitore;
/// 2. trasferitore a doppio comando: due pacchi sullo stesso bilico, il secondo pesato per differenza;
/// 3. intestatura di coda in double banking, divisione nello spazio libero tra i due pacchi;
/// 4. primo rullo dello svincolo sull'inverter di legatura.
/// </summary>
public class DoubleBankingDecisionsTests
{
    private static LineProject Model(string name) => SampleProjects.All.Single(m => m.Name.StartsWith(name, StringComparison.Ordinal)).Factory();

    private static void OnlyShortest(LineProject p, int count)
    {
        var t = p.PackageTypes.OrderBy(x => x.LengthMm).First();
        p.Infeed.TypeSequence = Enumerable.Repeat(t.Id, count).ToList();
        p.Infeed.PackageCount = count;
    }

    private static void LongWeighing(LineProject p)
    {
        foreach (var b in p.Scales) { b.StabilizationTimeS = 150; b.ConfirmTimeoutS = 400; }
    }

    private static void AddStop(LineProject p, DevicePlacement place)
    {
        p.IntermediateStops.Clear();
        p.IntermediateStops.Add(new IntermediateStopConfig
        {
            Id = "FM1", Name = "Fermo scomparente", Placement = place,
            PositionMm = StrappingAreaRules.StopPositionMm(p, place), AlignmentTimeS = 2.0
        });
    }

    private static string Dump(SimulationEngine e) =>
        string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(8).Select(x => x.ToString()));

    private static void Clean(SimulationEngine e)
    {
        Assert.True(e.CompletedNormally, e.FinishReason + "\n" + Dump(e));
        Assert.True(e.Collisions == 0, Dump(e));
        Assert.True(e.SlipEvents == 0, Dump(e) + "\n" + AroundFirstSlip(e));
    }

    /// <summary>Eventi e velocità delle zone attorno al primo strisciamento (per capirne la causa dalla CI).</summary>
    private static string AroundFirstSlip(SimulationEngine e)
    {
        var slip = e.Events.FirstOrDefault(x => x.Type == SimEventType.Slip);
        if (slip == null) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("zone: " + string.Join(" ", e.Zones.Select((z, i) => $"{i}={z.Id}[{z.Info.StartM:0.000}-{z.Info.EndM:0.000}]")) + $" split={e.TransferSplitM} primoRullo={e.FirstReleaseRollerEndM}");
        foreach (var x in e.Events.Where(x => x.TimeS >= slip.TimeS - 4 && x.TimeS <= slip.TimeS + 0.1).TakeLast(40)) sb.AppendLine("  " + x);
        foreach (var f in e.Recorder.Frames.Where(f => f.TimeS >= slip.TimeS - 2.5 && f.TimeS <= slip.TimeS + 0.1))
            sb.AppendLine($"  t={f.TimeS:0.00} v=[{string.Join(" ", f.ZoneSpeeds.Select(v => v.ToString("0.00")))}] valle={f.ValleSpeed:0.00} stati=[{string.Join(" ", f.ZoneStates)}] " +
                          string.Join(" ", f.Packages.Select(k => $"{k.Id}:{k.HeadM:0.000}/{k.LengthM:0}@{k.Velocity:0.00}")));
        return sb.ToString();
    }

    private static int Pairs(SimulationEngine e) =>
        e.Events.Count(x => x.Type == SimEventType.TransferPhase && x.Message.StartsWith("Trasferitore: sollevamento (", StringComparison.Ordinal));

    [Theory]
    [InlineData("3 legatrici")]
    [InlineData("2 legatrici - pacchi 6/9/12/18")]
    public void ThirdPackage_WaitsAtThePhotocell_WhenBothPlacesAreTaken(string model)
    {
        // rapporto del 2026-11-17: pacchi da 6 m in fila con la pesatura lunga, il terzo entrava e urtava il trasferitore
        var p = Model(model);
        AddStop(p, DevicePlacement.Center);
        OnlyShortest(p, 6);
        LongWeighing(p);
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Clean(e);
        Assert.Equal(0, e.ZoneOverfills);
        Assert.True(Pairs(e) >= 2, $"{Pairs(e)} cicli con due pacchi");
    }

    [Theory]
    [InlineData("1 legatrice")]
    [InlineData("2 legatrici - pacchi 6/9/12 m")]
    public void SameScale_SecondIsWeighedByDifference_WithTheDoubleCommandTransfer(string model)
    {
        // un solo bilico lungo come il trasferitore: prima si sarebbero sempre pesati da soli
        var p = Model(model);
        p.IntermediateStops.Clear();
        OnlyShortest(p, 6);
        LongWeighing(p);
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Clean(e);
        Assert.True(Pairs(e) >= 2, $"{Pairs(e)} cicli con due pacchi");
        Assert.Contains(e.Events, x => x.Type == SimEventType.TransferPhase && x.Message.Contains("metà a monte", StringComparison.Ordinal));
        Assert.Contains(e.Events, x => x.Type == SimEventType.WeightAcquired && x.Message.Contains("per differenza", StringComparison.Ordinal));
        foreach (var k in e.Packages.Where(k => k.MeasuredWeightKg.HasValue))
            Assert.True(Math.Abs(k.MeasuredWeightKg!.Value - k.WeightKg) <= 2 * Math.Max(1, p.Scales[0].ResolutionKg), $"{k.Id}: {k.MeasuredWeightKg} kg, vero {k.WeightKg} kg");
        Assert.True(e.Transfer.Cycles < e.Packages.Count + 1);
    }

    [Theory]
    [InlineData("3 legatrici")]
    [InlineData("2 legatrici - pacchi 6/9/12/18")]
    public void TailHeading_InDoubleBanking_SplitsInTheFreeSpaceBetweenThePackages(string model)
    {
        var p = Model(model);
        AddStop(p, DevicePlacement.End);
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        p.Simulation.Heading = StopReference.Tail;
        foreach (var t in p.PackageTypes) t.Heading = StopReference.Tail;
        OnlyShortest(p, 6);
        LongWeighing(p);
        var probe = ProjectSerializer.Clone(p);
        SimulationPreparation.Prepare(probe);
        var shortest = probe.PackageTypes.OrderBy(x => x.LengthMm).First();
        Assert.Equal(AccumulationMode.DoubleBanking, BankingOptions.Banking(probe, shortest));
        Assert.Equal(StopReference.Tail, BankingOptions.Heading(probe, shortest));
        double split = TransferSplitRule.SplitMm(probe);
        var stop = probe.IntermediateStops[0];
        // il secondo (coda sul fermo, testa + extra corsa) tutto a monte, il primo tutto a valle
        Assert.True(stop.PositionMm + shortest.LengthMm + stop.TailOvershootMm <= TransferSplitRule.SecondHeadMaxMm(probe, split) + 1e-6, $"divisione {split}");
        Assert.True(probe.Stopper.PositionMm - shortest.LengthMm >= TransferSplitRule.FirstTailMinMm(probe, split) - 1e-6, $"divisione {split}");

        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Clean(e);
        Assert.True(Pairs(e) >= 2, $"{Pairs(e)} cicli con due pacchi");
        Assert.Contains(e.Events, x => x.Type == SimEventType.StopPhase && x.Message.Contains("coda", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("3 legatrici")]
    [InlineData("2 legatrici - pacchi 6/9/12/18")]
    [InlineData("2 legatrici - pacchi 6/9/12 m")]
    [InlineData("1 legatrice")]
    public void FirstReleaseRoller_IsOnTheStrappingInverter_NoSlipAtTheStrappingExit(string model)
    {
        // rapporto del 2026-11-17: senza fermo scomparente, pacchi da 6 m e pesatura lunga, la testa entrava di 8 mm sullo
        // svincolo fermo durante l'ultima legatura di coda
        var p = Model(model);
        p.IntermediateStops.Clear();
        OnlyShortest(p, 6);
        LongWeighing(p);
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        var e = new SimulationEngine(p);
        Assert.NotNull(e.FirstReleaseRollerEndM);
        var rel = StrappingAreaRules.ReleaseModules(p).First();
        Assert.Equal((rel.StartMm + StrappingAreaRules.FirstReleaseRollerMm(rel)) / 1000.0, e.FirstReleaseRollerEndM!.Value, 6);
        Assert.Contains("primo rullo", StrappingAreaRules.DescribeDrive(p, rel));
        e.RunToCompletion();
        Clean(e);
    }

    [Theory]
    [InlineData("3 legatrici", false)]
    [InlineData("3 legatrici", true)]
    [InlineData("2 legatrici - pacchi 6/9/12/18", false)]
    public void DoubleBanking_PackagesAlwaysLeaveInPairs(string model, bool longWeighing)
    {
        // regola dell'utente del 2026-11-18: i pacchi che si possono abbinare partono sempre a coppia, anche con la legatura
        // lenta (il compagno arriva dopo più di un minuto dall'allineamento del primo)
        var p = Model(model);
        AddStop(p, DevicePlacement.Center);
        OnlyShortest(p, 8);
        if (longWeighing) LongWeighing(p);
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Clean(e);
        Assert.True(Pairs(e) == 4, $"{Pairs(e)} cicli con due pacchi su 8 pacchi\n" +
            string.Join("\n", e.Events.Where(x => x.Type is SimEventType.TransferPhase or SimEventType.TransferWaiting).Select(x => x.ToString()).Take(40)));
        Assert.DoesNotContain(e.Events, x => x.Type == SimEventType.TransferWaiting && x.Message.Contains("attesa gruppo scaduta", StringComparison.Ordinal));
    }

    [Fact]
    public void DoubleBanking_SinglePackageLeaves_OnlyWhenNoMaterialArrivesForAMinute()
    {
        // ingresso a intervallo fisso di 400 s: dopo un minuto senza materiale in ingresso il pacco solo parte
        var p = Model("3 legatrici");
        AddStop(p, DevicePlacement.Center);
        OnlyShortest(p, 3);
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        p.Infeed.Mode = InfeedMode.FixedInterval;
        p.Infeed.IntervalS = 400;
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Clean(e);
        Assert.Contains(e.Events, x => x.Type == SimEventType.TransferWaiting && x.Message.Contains("non arriva materiale", StringComparison.Ordinal));
        Assert.Equal(0, Pairs(e));
    }
}

