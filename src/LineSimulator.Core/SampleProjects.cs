using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Linee di esempio già configurate (usate anche dai test).</summary>
public static class SampleProjects
{
    public static IReadOnlyList<(string Name, Func<LineProject> Factory)> All { get; } = new List<(string, Func<LineProject>)>
    {
        ("Linea standard - 3 legatrici", CreateStandardLine),
        ("Linea con 1 legatrice", CreateSingleStrapperLine)
    };

    /// <summary>
    /// Linea di riferimento:
    /// INV1 ingresso (0-13 m) → INV2 accumulo (13-26 m) → INV3 legatura, 2 sezioni (26-42 m)
    /// → INV4 uscita con stopper a 55 m e trasferitore sotto il pacco in battuta.
    /// Pacco 12 m / 3000 kg con legature a 1500, 6000, 10500 mm dalla testa.
    /// </summary>
    public static LineProject CreateStandardLine()
    {
        var p = new LineProject { Name = "Linea standard - 3 legatrici", Description = "Linea di esempio con accumulo, stazione di legatura a 3 legatrici, stopper e trasferitore." };

        p.Inverters.Add(Inv("INV1", "Inverter 1 - Ingresso"));
        p.Inverters.Add(Inv("INV2", "Inverter 2 - Accumulo"));
        p.Inverters.Add(Inv("INV3", "Inverter 3 - Legatura"));
        p.Inverters.Add(Inv("INV4", "Inverter 4 - Uscita/Stopper"));

        p.Sections.Add(Sec("S1", "Via ingresso", 0, 13000, "INV1"));
        p.Sections.Add(Sec("S2", "Via accumulo", 13000, 13000, "INV2"));
        p.Sections.Add(Sec("S3", "Legatura A", 26000, 8000, "INV3"));
        p.Sections.Add(Sec("S4", "Legatura B", 34000, 8000, "INV3"));
        p.Sections.Add(Sec("S5", "Via uscita", 42000, 14000, "INV4"));

        p.Photocells.Add(Pc("FC1", "FC1 accumulo ingresso", 11500, "INV1", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC2", "FC2 accumulo", 24500, "INV2", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC3", "FC3 fine legatura", 40500, "INV3", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC4", "FC4 rallentamento stopper", 53500, "INV4", PhotocellAction.SlowDown));
        p.Photocells.Add(Pc("FC5", "FC5 presenza stopper", 54800, "", PhotocellAction.None));

        p.Strappers.Add(Str("L1", "Legatrice 1", 30500));
        p.Strappers.Add(Str("L2", "Legatrice 2", 35000));
        p.Strappers.Add(Str("L3", "Legatrice 3", 39500));

        p.StrappingStation = new StrappingStationConfig
        {
            IsEnabled = true,
            InverterId = "INV3",
            PositioningSpeed = 0.3,
            PositioningToleranceMm = 2,
            Strategy = StrappingStrategyMode.Automatic
        };

        p.Stopper = new StopperConfig
        {
            Id = "STP", Name = "Stopper", PositionMm = 55000, AlignmentTimeS = 2.0, HeadingTimeS = 0.5,
            AlignmentRollerSpeed = 0.1, IsRaised = true, MaxImpactSpeed = 0.25
        };
        p.Transfer = new TransferConfig
        {
            Id = "TRF", Name = "Trasferitore", PositionMm = 43000, LengthMm = 12000, LiftStrokeMm = 150,
            TranslationStrokeMm = 3000, TranslationSpeed = 0.5, LiftTimeS = 2, LowerTimeS = 2, DepositTimeS = 1,
            BlockInfeedUntilHome = true
        };

        p.PackageTypes.Add(new PackageType
        {
            Id = "P12", Name = "Pacco 12 m", LengthMm = 12000, WeightKg = 3000,
            StrapPositionsMm = new List<double> { 1500, 6000, 10500 }
        });

        p.Infeed = new InfeedConfig { PackageCount = 10, IntervalS = 30, MinimumGapMm = 1000, TypeSequence = new List<string> { "P12" } };
        p.Simulation = new SimulationSettings();
        return p;
    }

    /// <summary>Variante con una sola legatrice e zona di legatura più lunga (22 m).</summary>
    public static LineProject CreateSingleStrapperLine()
    {
        var p = CreateStandardLine();
        p.ProjectId = Guid.NewGuid().ToString("N");
        p.Name = "Linea con 1 legatrice";
        p.Description = "Una sola legatrice: legature eseguite in sequenza con riposizionamento del pacco.";
        p.Sections.Clear();
        p.Sections.Add(Sec("S1", "Via ingresso", 0, 13000, "INV1"));
        p.Sections.Add(Sec("S2", "Via accumulo", 13000, 13000, "INV2"));
        p.Sections.Add(Sec("S3", "Legatura A", 26000, 11000, "INV3"));
        p.Sections.Add(Sec("S4", "Legatura B", 37000, 11000, "INV3"));
        p.Sections.Add(Sec("S5", "Via uscita", 48000, 14000, "INV4"));
        p.Photocells.Clear();
        p.Photocells.Add(Pc("FC1", "FC1 accumulo ingresso", 11500, "INV1", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC2", "FC2 accumulo", 24500, "INV2", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC3", "FC3 fine legatura", 46500, "INV3", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC4", "FC4 rallentamento stopper", 59500, "INV4", PhotocellAction.SlowDown));
        p.Photocells.Add(Pc("FC5", "FC5 presenza stopper", 60800, "", PhotocellAction.None));
        p.Strappers.Clear();
        p.Strappers.Add(Str("L1", "Legatrice 1", 37000));
        p.Stopper.PositionMm = 61000;
        p.Transfer.PositionMm = 49000;
        return p;
    }

    private static InverterConfig Inv(string id, string name) => new()
    {
        Id = id, Name = name, LowSpeed = 0.15, HighSpeed = 0.6, Acceleration = 0.5, Deceleration = 0.6, DefaultSpeed = SpeedLevel.High
    };

    private static RollerSection Sec(string id, string name, double start, double length, string inv) => new()
    {
        Id = id, Name = name, StartMm = start, LengthMm = length, WidthMm = 1200,
        RollerPitchMm = 300, RollerCount = (int)Math.Floor(length / 300.0) + 1 - (length % 300 == 0 ? 1 : 0),
        RollerDiameterMm = 150, InverterId = inv
    };

    private static PhotocellConfig Pc(string id, string name, double pos, string inv, PhotocellAction action) => new()
    {
        Id = id, Name = name, PositionMm = pos, ControlledInverterId = inv, Action = action, ResponseTimeMs = 20
    };

    private static StrapperConfig Str(string id, string name, double pos) => new()
    {
        Id = id, Name = name, PositionMm = pos, FootprintLengthMm = 1200, PreparationTimeS = 2, StrappingTimeS = 8, ReturnTimeS = 2
    };
}
