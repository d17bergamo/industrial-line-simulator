using System.Reflection;
using System.Text.Json.Nodes;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Infrastructure;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Banking (single/double) e intestatura (testa/coda) scelti nella sezione Simulazioni.</summary>
public class BankingHeadingTests
{
    private static string Diag(SimulationEngine e) =>
        $"{e.FinishReason} t={e.Time:0.0}\n" + string.Join("\n", e.Events.Where(x => x.Severity >= EventSeverity.Warning).Take(30));

    private static LineProject Full(AccumulationMode banking, StopReference heading, string type = "P6", int count = 3)
    {
        var p = LegacySamples.CreateFullProcessLine();
        p.Simulation.Banking = banking;
        p.Simulation.Heading = heading;
        p.Infeed.TypeSequence = new List<string> { type };
        p.Infeed.PackageCount = count;
        return p;
    }

    /// <summary>Esegue e registra, a ogni sollevamento del trasferitore, testa e coda dei pacchi prelevati.</summary>
    private static (SimulationEngine E, List<List<(int Seq, double Head, double Tail)>> Lifts) Run(LineProject p)
    {
        var e = new SimulationEngine(p);
        var lifts = new List<List<(int, double, double)>>();
        bool lifting = false;
        while (e.Step())
        {
            bool now = e.Transfer.State == TransferState.Lifting;
            if (now && !lifting) lifts.Add(e.Transfer.Packages.Select(k => (k.Sequence, k.HeadM, k.TailM)).ToList());
            lifting = now;
        }
        return (e, lifts);
    }

    [Fact]
    public void BankingAndHeading_AreSimulationSettings_NotPackageOrConfigurationFields()
    {
        static bool Ui(Type t, string name) => t.GetProperty(name)!.GetCustomAttribute<UiPropertyAttribute>() != null;
        Assert.False(Ui(typeof(SimulationSettings), nameof(SimulationSettings.Banking)));
        Assert.False(Ui(typeof(SimulationSettings), nameof(SimulationSettings.Heading)));
        Assert.False(Ui(typeof(IntermediateStopConfig), nameof(IntermediateStopConfig.Reference)));
        Assert.False(Ui(typeof(TransferConfig), nameof(TransferConfig.BatchSize)));
        Assert.DoesNotContain(typeof(PackageType).GetProperties(), x => x.PropertyType == typeof(AccumulationMode) || x.PropertyType == typeof(StopReference));
    }

    [Fact]
    public void SingleBanking_Head_OnePackagePerCycle_AgainstFinalStopper()
    {
        var (e, lifts) = Run(Full(AccumulationMode.SingleBanking, StopReference.Head));
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(AccumulationMode.SingleBanking, e.EffectiveBanking);
        Assert.Empty(e.IntermediateStops);   // fermo intermedio sempre abbassato
        Assert.Equal(3, e.Transfer.Cycles);
        Assert.All(lifts, l => Assert.InRange(Assert.Single(l).Head, 56.5 - 0.003, 56.5 + 1e-6));
        Assert.Equal(0, e.Collisions);
    }

    [Fact]
    public void DoubleBanking_Head_TwoPackagesPerCycle_SecondOnIntermediateStop()
    {
        var (e, lifts) = Run(Full(AccumulationMode.DoubleBanking, StopReference.Head, count: 4));
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(2, e.Transfer.Cycles);
        Assert.All(lifts, l =>
        {
            Assert.Equal(2, l.Count);
            var o = l.OrderByDescending(x => x.Head).ToList();
            Assert.InRange(o[0].Head, 56.5 - 0.003, 56.5 + 1e-6);
            Assert.InRange(o[1].Head, 50.1 - 0.003, 50.1 + 1e-6);
        });
        Assert.Equal(0, e.Collisions);
    }

    [Fact]
    public void SingleBanking_Tail_PassesStop_StopRises_PackageReturnsOntoItsTail()
    {
        var (e, lifts) = Run(Full(AccumulationMode.SingleBanking, StopReference.Tail));
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(StopReference.Tail, e.EffectiveHeading);
        Assert.Equal(3, e.Transfer.Cycles);
        Assert.All(lifts, l => Assert.InRange(Assert.Single(l).Tail, 50.1 - 1e-6, 50.1 + 0.003));
        Assert.Contains(e.Events, x => x.Type == SimEventType.StopPhase && x.Message.Contains("ritorno indietro", StringComparison.Ordinal));
        Assert.Contains(e.Events, x => x.Type == SimEventType.StopPhase && x.Message.Contains("in battuta di coda", StringComparison.Ordinal));
        Assert.Contains(e.Events, x => x.Type == SimEventType.InverterCommand && x.Message.Contains("RITORNO INDIETRO", StringComparison.Ordinal));
        Assert.Equal(0, e.Collisions);
        Assert.Equal(0, e.PositioningErrors);
    }

    [Fact]
    public void SingleBanking_Tail_TooLongPackage_FallsBackToHeadOnFinalStopper()
    {
        var (e, lifts) = Run(Full(AccumulationMode.SingleBanking, StopReference.Tail, type: "P12", count: 2));
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.All(lifts, l => Assert.InRange(Assert.Single(l).Head, 56.5 - 0.003, 56.5 + 1e-6));
        Assert.Contains(e.Events, x => x.Severity == EventSeverity.Warning && x.Message.Contains("intestatura di coda non possibile", StringComparison.Ordinal));
    }

    [Fact]
    public void DoubleBanking_Tail_SecondPackageTailOnStopAtTransferEnd()
    {
        var p = Full(AccumulationMode.DoubleBanking, StopReference.Tail, count: 2);
        p.IntermediateStops[0].PositionMm = 44100;   // fermo all'estremità del trasferitore
        var (e, lifts) = Run(p);
        Assert.True(e.CompletedNormally, Diag(e));
        var l = Assert.Single(lifts);
        var o = l.OrderByDescending(x => x.Head).ToList();
        Assert.InRange(o[0].Head, 56.5 - 0.003, 56.5 + 1e-6);
        Assert.InRange(o[1].Tail, 44.1 - 1e-6, 44.1 + 0.003);
        Assert.Contains(e.Events, x => x.Message.Contains("ritorno indietro", StringComparison.Ordinal));
        Assert.Equal(0, e.Collisions);
    }

    [Fact]
    public void WithoutIntermediateStop_TailIsNotAvailable_AndIsResetToHead()
    {
        var p = LegacySamples.CreateStandardLine();
        Assert.False(BankingOptions.TailHeadingAvailable(p));
        Assert.True(BankingOptions.TailHeadingAvailable(LegacySamples.CreateFullProcessLine()));
        p.Simulation.Heading = StopReference.Tail;
        var r = SimulationPreparation.Prepare(p);
        Assert.Equal(StopReference.Head, p.Simulation.Heading);
        Assert.Contains(r.Changes, c => c.Contains("Intestatura di coda non disponibile", StringComparison.Ordinal));
        // Anche se il progetto arriva al motore con "coda", il motore usa la testa
        var q = LegacySamples.CreateStandardLine();
        q.Simulation.Heading = StopReference.Tail;
        q.Infeed.PackageCount = 2;
        var e = new SimulationEngine(q);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(StopReference.Head, e.EffectiveHeading);
    }

    private static LineProject StandardWith6m(AccumulationMode banking, int count)
    {
        var p = LegacySamples.CreateStandardLine();
        p.PackageTypes.Add(new PackageType { Id = "P6", Name = "Pacco 6 m", LengthMm = 6000, WeightKg = 1500, StrapPositionsMm = new List<double> { 750, 3000, 5250 } });
        p.Infeed.TypeSequence = new List<string> { "P6" };
        p.Infeed.PackageCount = count;
        p.Simulation.Banking = banking;
        return p;
    }

    [Fact]
    public void DoubleBanking_6m_WithoutIntermediateStop_TwoPackagesStopOnRollers_ThenTransferTakesBoth()
    {
        var (e, lifts) = Run(StandardWith6m(AccumulationMode.DoubleBanking, 4));
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(AccumulationMode.DoubleBanking, e.EffectiveBanking);
        Assert.Equal(2, e.Transfer.Cycles);
        Assert.Equal(2, lifts.Count);
        Assert.All(lifts, l =>
        {
            Assert.Equal(2, l.Count);
            var o = l.OrderByDescending(x => x.Head).ToList();
            Assert.InRange(o[0].Head, 55.0 - 0.003, 55.0 + 1e-6);            // primo in battuta sullo stopper
            double second = 55.0 - 6.0 - TransferConfig.DefaultDoubleBankingGapMm / 1000.0;   // secondo 500 mm dietro (posizionamento)
            Assert.InRange(o[1].Head, second - 0.004, second + 0.001);
            Assert.True(o[1].Tail >= 43.0 - 0.5 - 1e-6);                     // sbalzo entro 500 mm
        });
        Assert.All(e.Packages, k => Assert.Equal(StrapStatus.Done, k.StrapStatus));
        Assert.Equal(0, e.Collisions);
        Assert.Contains(e.Events, x => x.Message.Contains("secondo pacco in double banking", StringComparison.Ordinal));

        // Confronto: in single banking servono 4 cicli del trasferitore
        var (s, _) = Run(StandardWith6m(AccumulationMode.SingleBanking, 4));
        Assert.True(s.CompletedNormally, Diag(s));
        Assert.Equal(4, s.Transfer.Cycles);
    }

    [Fact]
    public void DoubleBanking_6m_FullLine_WithIntermediateStop_TwoPackagesPerCycle()
    {
        var (e, lifts) = Run(Full(AccumulationMode.DoubleBanking, StopReference.Head, count: 6));
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(3, e.Transfer.Cycles);
        Assert.All(lifts, l => Assert.Equal(2, l.Count));
    }

    [Fact]
    public void DoubleBanking_12m_WithoutIntermediateStop_OnePackagePerCycle()
    {
        var p = LegacySamples.CreateStandardLine();
        p.Simulation.Banking = AccumulationMode.DoubleBanking;
        p.Infeed.PackageCount = 2;
        var (e, lifts) = Run(p);
        Assert.True(e.CompletedNormally, Diag(e));
        Assert.Equal(2, e.Transfer.Cycles);
        Assert.All(lifts, l => Assert.Single(l));
    }

    [Fact]
    public void OldProject_BatchSizeAndStopReference_BecomeSimulationSettings()
    {
        var p = LegacySamples.CreateFullProcessLine();
        p.IntermediateStops[0].Reference = StopReference.Tail;
        var node = JsonNode.Parse(ProjectSerializer.Serialize(p))!.AsObject();
        var sim = node.First(kv => kv.Key.Equals("simulation", StringComparison.OrdinalIgnoreCase)).Value!.AsObject();
        foreach (var k in sim.Select(kv => kv.Key).Where(k => k.Equals("banking", StringComparison.OrdinalIgnoreCase) || k.Equals("heading", StringComparison.OrdinalIgnoreCase)).ToList())
            sim.Remove(k);
        var q = ProjectSerializer.Deserialize(node.ToJsonString());
        Assert.Equal(AccumulationMode.DoubleBanking, q.Simulation.Banking);
        Assert.Equal(StopReference.Tail, q.Simulation.Heading);

        // Con le impostazioni salvate, valgono quelle
        p.Simulation.Banking = AccumulationMode.SingleBanking;
        p.Simulation.Heading = StopReference.Head;
        var r = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p));
        Assert.Equal(AccumulationMode.SingleBanking, r.Simulation.Banking);
        Assert.Equal(StopReference.Head, r.Simulation.Heading);
    }

    [Fact]
    public void Outputs_DescribeTheChosenBankingAndHeading()
    {
        var p = Full(AccumulationMode.SingleBanking, StopReference.Tail);
        var logic = ControlLogicGenerator.Generate(p);
        Assert.Contains(logic.Rules, r => r.Id == "FM1.RITORNO");
        Assert.Contains(logic.Rules, r => r.Id == "TRF.SOLLEVA" && r.Description.Contains("Single banking", StringComparison.Ordinal));
    }
}
