using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>C5 (revisore): un fermo scomparente sollevato ferma qualunque pacco; per un pacco non assegnato è una collisione.</summary>
public class RaisedStopTests
{
    [Fact]
    public void RaisedStop_StopsOrFlagsAnyPackage()
    {
        var p = SampleProjects.All.Single(m => m.Name.StartsWith("3 legatrici", StringComparison.Ordinal)).Factory();
        p.IntermediateStops.Clear();
        p.IntermediateStops.Add(new IntermediateStopConfig
        {
            Id = "FM1", Name = "Fermo scomparente", Placement = DevicePlacement.Center,
            PositionMm = StrappingAreaRules.StopPositionMm(p, DevicePlacement.Center), AlignmentTimeS = 2.0
        });
        var t = p.PackageTypes.OrderBy(x => x.LengthMm).First();
        p.Infeed.TypeSequence = Enumerable.Repeat(t.Id, 6).ToList();
        p.Infeed.PackageCount = 6;
        BankingOptions.SetBankingAll(p, AccumulationMode.DoubleBanking);
        var e = new SimulationEngine(p);
        var s = e.IntermediateStops.Single();
        // aspetta il fermo sollevato per il suo pacco, con un altro pacco sui rulli a monte (non in legatura)
        PackageRuntime? other = null;
        for (int i = 0; i < 400_000 && other == null && e.Step(); i++)
            if (s.Raised && s.Package != null)
                other = e.Packages.FirstOrDefault(q => q != s.Package && q.OnRollers && q != e.Station?.Package && q.HeadM < s.PositionM - 0.5);
        Assert.NotNull(other);
        int before = e.Collisions;
        other!.HeadM = s.PositionM - 0.002;                               // lo si porta a ridosso del fermo, in movimento
        other.Velocity = 0.3;
        for (int i = 0; i < 20; i++) e.Step();
        Assert.True(other.HeadM <= s.PositionM + 1e-6, $"testa a {other.HeadM:0.000} m, fermo a {s.PositionM:0.000} m");
        Assert.True(e.Collisions > before);
        Assert.Contains(e.Events, x => x.Type == SimEventType.Collision && x.ComponentId == s.Id && x.PackageId == other.Id);
    }
}
