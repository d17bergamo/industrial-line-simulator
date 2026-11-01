using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Linee minime costruite per i test.</summary>
internal static class TestLines
{
    /// <summary>
    /// Linea a una zona (INV1, 0-30 m), stopper a 29 m, trasferitore sotto il pacco in battuta,
    /// nessuna legatura. Velocità alta 0.6, bassa 0.15, acc 0.5, dec 0.6.
    /// </summary>
    public static LineProject SingleZone(int packages = 1)
    {
        var p = new LineProject { Name = "Test una zona" };
        p.Inverters.Add(new InverterConfig { Id = "INV1", Name = "INV1", LowSpeed = 0.15, HighSpeed = 0.6, Acceleration = 0.5, Deceleration = 0.6 });
        p.Sections.Add(new RollerSection { Id = "S1", Name = "S1", StartMm = 0, LengthMm = 30000, RollerCount = 100, RollerPitchMm = 300, RollerDiameterMm = 150, InverterId = "INV1" });
        p.StrappingStation = new StrappingStationConfig { IsEnabled = false };
        p.Stopper = new StopperConfig { Id = "STP", Name = "Stopper", PositionMm = 29000, AlignmentTimeS = 2.0, HeadingTimeS = 0.5, AlignmentRollerSpeed = 0.1, MaxImpactSpeed = 0.25 };
        p.Transfer = new TransferConfig
        {
            Id = "TRF", Name = "Trasferitore", PositionMm = 17000, LengthMm = 12000, TranslationStrokeMm = 3000, TranslationSpeed = 0.5,
            LiftTimeS = 2, LowerTimeS = 2, DepositTimeS = 1
        };
        p.PackageTypes.Add(new PackageType { Id = "P12", Name = "P12", LengthMm = 12000, WeightKg = 3000, StrapPositionsMm = new List<double>() });
        p.Infeed = new InfeedConfig { PackageCount = packages, IntervalS = 20, MinimumGapMm = 1000 };
        p.Simulation = new SimulationSettings { DeadlockTimeoutS = 30, MaxSimulationTimeS = 3600 };
        return p;
    }

    /// <summary>Due zone senza fotocellule di accumulo: configurazione volutamente pericolosa.</summary>
    public static LineProject TwoZonesWithoutAccumulation()
    {
        var p = SingleZone(2);
        p.Sections.Clear();
        p.Inverters.Add(new InverterConfig { Id = "INV2", Name = "INV2", LowSpeed = 0.15, HighSpeed = 0.6, Acceleration = 0.5, Deceleration = 0.6 });
        p.Sections.Add(new RollerSection { Id = "S1", Name = "S1", StartMm = 0, LengthMm = 14000, RollerCount = 46, RollerPitchMm = 300, RollerDiameterMm = 150, InverterId = "INV1" });
        p.Sections.Add(new RollerSection { Id = "S2", Name = "S2", StartMm = 14000, LengthMm = 14000, RollerCount = 46, RollerPitchMm = 300, RollerDiameterMm = 150, InverterId = "INV2" });
        p.Stopper.PositionMm = 27000;
        p.Transfer.PositionMm = 15000;
        p.Transfer.LiftTimeS = 120; // il pacco resta a lungo allo stopper
        p.Infeed.IntervalS = 10;
        p.Simulation.DeadlockTimeoutS = 300;
        return p;
    }

    public static SimEvent First(SimulationEngine e, SimEventType type) => e.Events.First(x => x.Type == type);
}
