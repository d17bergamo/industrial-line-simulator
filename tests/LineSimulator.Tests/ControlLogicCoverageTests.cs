using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// B6, B7 ed E (revisore): la logica di controllo JSON e il documento Word descrivono la logica vera del motore.
/// Ogni decisione registrata dal motore con <c>Link</c> deve avere una regola corrispondente (chiave in <c>EngineKeys</c>).
/// </summary>
public class ControlLogicCoverageTests
{
    private static LineProject Sample(string name) => SampleProjects.All.Single(m => m.Name.StartsWith(name, StringComparison.Ordinal)).Factory();

    private static void Only(LineProject p, Func<PackageType, bool> pick, int count)
    {
        var t = p.PackageTypes.Where(pick).OrderBy(x => x.LengthMm).First();
        p.Infeed.TypeSequence = Enumerable.Repeat(t.Id, count).ToList();
        p.Infeed.PackageCount = count;
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

    public static IEnumerable<object[]> Cases()
    {
        foreach (var m in SampleProjects.All) yield return new object[] { m.Name, "esempio" };
        yield return new object[] { "3 legatrici", "double-centro" };
        yield return new object[] { "3 legatrici", "coda-estremità" };
        yield return new object[] { "3 legatrici", "double-senza-fermo" };
        yield return new object[] { "3 legatrici", "svincolo-corto-24m" };
        yield return new object[] { "3 legatrici", "svincolo-75m" };
    }

    private static LineProject Build(string sample, string variant)
    {
        var p = Sample(sample);
        switch (variant)
        {
            case "esempio":
                p.Infeed.PackageCount = Math.Min(Math.Max(p.Infeed.PackageCount, 6), 8);
                break;
            case "double-centro":
                AddStop(p, DevicePlacement.Center);
                Only(p, _ => true, 6);
                BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
                break;
            case "coda-estremità":
                AddStop(p, DevicePlacement.End);
                Only(p, _ => true, 4);
                foreach (var t in p.PackageTypes) t.Heading = StopReference.Tail;
                break;
            case "double-senza-fermo":
                p.IntermediateStops.Clear();
                Only(p, _ => true, 6);
                BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
                break;
            case "svincolo-corto-24m":
            {
                var rel = StrappingAreaRules.ReleaseModules(p).Single();
                double oldEnd = rel.EndMm;
                LineReflow.Fit(rel, rel.StartMm, rel.StartMm + 4 * rel.RollerPitchMm);
                LineReflow.AfterResize(p, rel, oldEnd);
                Only(p, t => t.LengthMm >= 24000 - 1, 4);
                break;
            }
            case "svincolo-75m":
            {
                var rel = StrappingAreaRules.ReleaseModules(p).Single();
                double oldEnd = rel.EndMm;
                LineReflow.Fit(rel, rel.StartMm, rel.StartMm + 60 * rel.RollerPitchMm);
                LineReflow.AfterResize(p, rel, oldEnd);
                Only(p, t => t.LengthMm >= 12000 - 1, 6);
                break;
            }
        }
        return p;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ControlLogic_CoversEveryEngineLink(string sample, string variant)
    {
        var p = Build(sample, variant);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        var keys = e.Recorder.Links.Select(l => l.RuleId).Distinct().ToList();
        Assert.NotEmpty(keys);
        var doc = ControlLogicGenerator.Generate(p);
        var covered = doc.Rules.SelectMany(r => r.EngineKeys).ToHashSet(StringComparer.Ordinal);
        var missing = keys.Where(k => !covered.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"{sample} / {variant}: decisioni del motore senza regola nella logica di controllo: {string.Join(", ", missing)}");
        foreach (var r in doc.Rules) Assert.NotNull(ConditionText.Parse(r.Condition));
    }

    [Fact]
    public void ControlLogic_TransferStop_WaitsForTheTransferAtHome()
    {
        var p = Sample("3 legatrici");
        _ = new SimulationEngine(p);
        var doc = ControlLogicGenerator.Generate(p);
        var stop = doc.Rules.First(r => r.Id.Contains(".ARRESTO_TRASFERITORE_", StringComparison.Ordinal));
        Assert.Contains("!TRANSFER_HOME", stop.Condition, StringComparison.Ordinal);
        Assert.Contains(doc.Rules, r => r.Id.EndsWith(".INGRESSO", StringComparison.Ordinal) && r.Condition.Contains("SIG(PRECEDENTE_LEGATO)", StringComparison.Ordinal));
        var strap = new LineTopology(p).StrappingZone!;
        Assert.Contains("primo rullo", doc.Inverters.Single(x => x.Id == strap.Id).Modules, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionalSpec_BankingTextMatchesThePackageTable()
    {
        var p = Sample("3 legatrici");
        AddStop(p, DevicePlacement.Center);
        var shortest = p.PackageTypes.OrderBy(t => t.LengthMm).First();
        foreach (var t in p.PackageTypes) t.Banking = t == shortest ? AccumulationMode.DoubleBanking : AccumulationMode.SingleBanking;
        _ = new SimulationEngine(p);
        string text = FunctionalSpecText(p);
        foreach (var t in p.PackageTypes)
        {
            string expected = BankingOptions.Banking(p, t) == AccumulationMode.DoubleBanking ? "double banking" : "single banking";
            Assert.Contains($"{t.Id} ({t.LengthMm:0} mm): {expected}", text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("trasferitore abbassato", text, StringComparison.Ordinal);
    }

    private static string FunctionalSpecText(LineProject p)
    {
        var bytes = FunctionalSpecExporter.Build(p, null);
        using var ms = new MemoryStream(bytes);
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ms, false);
        return doc.MainDocumentPart!.Document.Body!.InnerText;
    }
}
