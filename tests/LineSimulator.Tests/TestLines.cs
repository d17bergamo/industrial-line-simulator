using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Linee minime costruite per i test.</summary>
internal static class TestLines
{
    /// <summary>
    /// Linea a un modulo (M1, 0-30 m), stopper a 29 m, trasferitore sotto il pacco in battuta,
    /// nessuna legatura. Velocità alta 0.6, bassa 0.15, acc 0.5, dec 0.6.
    /// </summary>
    public static LineProject SingleZone(int packages = 1)
    {
        var p = new LineProject { Name = "Test una zona" };
        p.Sections.Add(Module("M1", 0, 30000, 100));
        p.Photocells.Add(new PhotocellConfig { Id = "FC0", Name = "Ingresso", PositionMm = 300, IsEntryPhotocell = true });
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
        p.Sections.Add(Module("M1", 0, 14000, 46));
        p.Sections.Add(Module("M2", 14000, 14000, 46));
        p.Stopper.PositionMm = 27000;
        p.Transfer.PositionMm = 15000;
        p.Transfer.LiftTimeS = 120; // il pacco resta a lungo allo stopper
        p.Infeed.IntervalS = 10;
        p.Simulation.DeadlockTimeoutS = 300;
        return p;
    }

    /// <summary>Modulo di via a rulli con inverter: alta 0.6, bassa 0.15, acc 0.5, dec 0.6.</summary>
    public static RollerSection Module(string id, double start, double length, int rollers) => new()
    {
        Id = id, Name = id, StartMm = start, LengthMm = length, RollerCount = rollers, RollerPitchMm = 300, RollerDiameterMm = 150,
        LowSpeed = 0.15, HighSpeed = 0.6, Acceleration = 0.5, Deceleration = 0.6
    };

    public static SimEvent First(SimulationEngine e, SimEventType type) => e.Events.First(x => x.Type == type);
}
