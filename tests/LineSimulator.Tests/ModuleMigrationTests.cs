using LineSimulator.Core;
using LineSimulator.Infrastructure;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Moduli di via a rulli = inverter: migrazione dei progetti con inverter e sezioni separati.</summary>
public class ModuleMigrationTests
{
    private const string V2Json = """
    {
      "schemaVersion": 2,
      "name": "Linea v2",
      "inverters": [
        { "id": "INV1", "name": "Inverter ingresso", "lowSpeed": 0.2, "highSpeed": 0.8, "acceleration": 0.4, "deceleration": 0.7, "defaultSpeed": "High", "isEnabled": true, "autoStopWhenEmpty": false },
        { "id": "INV2", "name": "Inverter uscita", "lowSpeed": 0.1, "highSpeed": 0.5, "acceleration": 0.3, "deceleration": 0.5, "defaultSpeed": "Low", "isEnabled": true }
      ],
      "sections": [
        { "id": "S1", "name": "Sezione 1", "startMm": 0, "lengthMm": 10000, "rollerCount": 34, "rollerPitchMm": 300, "rollerDiameterMm": 150, "inverterId": "INV1" },
        { "id": "S2", "name": "Sezione 2", "startMm": 10000, "lengthMm": 8000, "rollerCount": 27, "rollerPitchMm": 300, "rollerDiameterMm": 150, "inverterId": "INV2" },
        { "id": "S3", "name": "Sezione 3", "startMm": 18000, "lengthMm": 8000, "rollerCount": 27, "rollerPitchMm": 300, "rollerDiameterMm": 150, "inverterId": "INV2", "isEnabled": true }
      ],
      "photocells": [
        { "id": "FC0", "positionMm": 300, "isEntryPhotocell": true },
        { "id": "FC1", "positionMm": 8000, "controlledInverterId": "INV1", "action": "Accumulation" }
      ],
      "strappingStation": { "isEnabled": false },
      "stopper": { "id": "STP", "name": "Stopper", "positionMm": 25000 },
      "transfer": { "id": "TRF", "name": "Trasferitore", "positionMm": 14000, "lengthMm": 11000 },
      "packageTypes": [ { "id": "P10", "lengthMm": 10000, "weightKg": 2000, "strapPositionsMm": [] } ],
      "infeed": { "packageCount": 1, "intervalS": 30 }
    }
    """;

    [Fact]
    public void V2Project_SectionsOfSameInverterBecomeOneModule()
    {
        var p = ProjectSerializer.Deserialize(V2Json);
        Assert.Equal(LineProject.CurrentSchemaVersion, p.SchemaVersion);
        Assert.Equal(2, p.Sections.Count);

        var m1 = p.Sections.Single(s => s.Id == "INV1");
        Assert.Equal("Inverter ingresso", m1.Name);
        Assert.Equal(0, m1.StartMm);
        Assert.Equal(10000, m1.LengthMm);
        Assert.Equal(0.8, m1.HighSpeed);
        Assert.Equal(0.7, m1.Deceleration);
        Assert.False(m1.AutoStopWhenEmpty);

        var m2 = p.Sections.Single(s => s.Id == "INV2");
        Assert.Equal(10000, m2.StartMm);
        Assert.Equal(16000, m2.LengthMm);
        Assert.Equal(54, m2.RollerCount);
        Assert.Equal(0.5, m2.HighSpeed);
        Assert.Equal(SpeedLevel.Low, m2.DefaultSpeed);
        Assert.True(m2.AutoStopWhenEmpty);

        // I riferimenti all'inverter restano validi: ora indicano il modulo
        var topo = new LineTopology(p);
        Assert.Equal(2, topo.Zones.Count);
        Assert.Same(topo.Zone("INV1"), topo.ZoneAt(5.0));
        Assert.Equal(0.8, topo.Zone("INV1")!.Inverter.HighSpeed);
        Assert.DoesNotContain(ProjectValidator.Validate(p).Errors, e => e.Code is "PC_INV" or "SEC_INV" or "ZONE_SPLIT");
    }

    [Fact]
    public void ModuleProject_RoundTripsWithoutInverterList()
    {
        var p = SampleProjects.CreateStandardLine();
        string json = ProjectSerializer.Serialize(p);
        Assert.DoesNotContain("\"inverters\"", json);
        var q = ProjectSerializer.Deserialize(json);
        Assert.Equal(json, ProjectSerializer.Serialize(q));
        Assert.Equal(4, q.Sections.Count);
    }

    [Fact]
    public void EachModuleIsOneControlZone()
    {
        var p = SampleProjects.CreateStandardLine();
        var topo = new LineTopology(p);
        Assert.Equal(p.Sections.Count, topo.Zones.Count);
        foreach (var s in p.Sections)
        {
            var z = topo.Zone(s.Id)!;
            Assert.Equal(s.StartMm / 1000.0, z.StartM, 9);
            Assert.Equal(s.EndMm / 1000.0, z.EndM, 9);
            Assert.Equal(s.HighSpeed, z.Inverter.HighSpeed);
        }
    }
}
