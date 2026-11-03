using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Linee di esempio già configurate (usate anche dai test).</summary>
public static class SampleProjects
{
    public static IReadOnlyList<(string Name, Func<LineProject> Factory)> All { get; } = new List<(string, Func<LineProject>)>
    {
        ("Linea standard - 3 legatrici", CreateStandardLine),
        ("Linea con 1 legatrice", CreateSingleStrapperLine),
        ("Linea completa - KNCA, KNOF, compattatore, fermi, bilici", CreateFullProcessLine)
    };

    /// <summary>
    /// Linea di riferimento:
    /// M1 ingresso (0-13 m) → M2 accumulo (13-26 m) → M3 legatura (26-42 m)
    /// → M4 uscita con stopper a 55 m e trasferitore sotto il pacco in battuta.
    /// Pacco 12 m / 3000 kg con legature a 1500, 6000, 10500 mm dalla testa.
    /// </summary>
    public static LineProject CreateStandardLine() => Complete(BuildStandardLine());

    /// <summary>Completa l'esempio con i sensori di processo obbligatori (aggiunti in automatico).</summary>
    private static LineProject Complete(LineProject p)
    {
        ProjectNormalizer.EnsureProcessSensors(p);
        return p;
    }

    private static LineProject BuildStandardLine()
    {
        var p = new LineProject { Name = "Linea standard - 3 legatrici", Description = "Linea di esempio con accumulo, stazione di legatura a 3 legatrici, stopper e trasferitore." };


        p.Sections.Add(Mod("M1", "Modulo 1 - Ingresso", 0, 13000));
        p.Sections.Add(Mod("M2", "Modulo 2 - Accumulo", 13000, 13000));
        p.Sections.Add(Mod("M3", "Modulo 3 - Legatura", 26000, 16000));
        p.Sections.Add(Mod("M4", "Modulo 4 - Uscita/Stopper", 42000, 14000));

        p.Photocells.Add(Entry());
        p.Photocells.Add(Pc("FC1", "FC1 accumulo ingresso", 11500, "M1", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC2", "FC2 accumulo", 24500, "M2", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC3", "FC3 fine legatura", 40500, "M3", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC4", "FC4 rallentamento stopper", 53500, "M4", PhotocellAction.SlowDown));
        p.Photocells.Add(Pc("FC5", "FC5 presenza stopper", 54800, "", PhotocellAction.None));

        p.Strappers.Add(Str("L1", "Legatrice 1", 30500));
        p.Strappers.Add(Str("L2", "Legatrice 2", 35000));
        p.Strappers.Add(Str("L3", "Legatrice 3", 39500));

        p.StrappingStation = new StrappingStationConfig
        {
            IsEnabled = true,
            InverterId = "M3",
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
        var p = BuildStandardLine();
        p.ProjectId = Guid.NewGuid().ToString("N");
        p.Name = "Linea con 1 legatrice";
        p.Description = "Una sola legatrice: legature eseguite in sequenza con riposizionamento del pacco.";
        p.Sections.Clear();
        p.Sections.Add(Mod("M1", "Modulo 1 - Ingresso", 0, 13000));
        p.Sections.Add(Mod("M2", "Modulo 2 - Accumulo", 13000, 13000));
        p.Sections.Add(Mod("M3", "Modulo 3 - Legatura", 26000, 22000));
        p.Sections.Add(Mod("M4", "Modulo 4 - Uscita/Stopper", 48000, 14000));
        p.Photocells.Clear();
        p.Photocells.Add(Entry());
        p.Photocells.Add(Pc("FC1", "FC1 accumulo ingresso", 11500, "M1", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC2", "FC2 accumulo", 24500, "M2", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC3", "FC3 fine legatura", 46500, "M3", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC4", "FC4 rallentamento stopper", 59500, "M4", PhotocellAction.SlowDown));
        p.Photocells.Add(Pc("FC5", "FC5 presenza stopper", 60800, "", PhotocellAction.None));
        p.Strappers.Clear();
        p.Strappers.Add(Str("L1", "Legatrice 1", 37000));
        p.Stopper.PositionMm = 61000;
        p.Transfer.PositionMm = 49000;
        return Complete(p);
    }

    /// <summary>
    /// Linea completa con tutte le logiche di processo:
    /// M1 ingresso (0-13 m, fotocellula di ingresso FC0) → M2 trasporto (13-27 m)
    /// → M3 legatura con stringifascio KNOF, compattatore laterale e 3 legatrici KNCA 7/800 (27-43 m)
    /// → M4 accumulo/trasferitore 12,5 m con fermo intermedio (intestatura di testa) e stopper finale
    /// → trasferitore a 2 pacchi → 2 bilici di pesatura.
    /// Pacchi misti 6 m / 6 m / 12 m / 4 m.
    /// </summary>
    public static LineProject CreateFullProcessLine()
    {
        var p = new LineProject
        {
            Name = "Linea completa - KNCA, KNOF, compattatore, fermi, bilici",
            Description = "Fotocellula di ingresso, arresto automatico inverter, stringifascio KNOF, compattatore, legatrici KNCA 7/800, accumulo 12 m con fermo intermedio, trasferitore a 2 pacchi e bilici."
        };

        p.Sections.Add(Mod("M1", "Modulo 1 - Ingresso", 0, 13000));
        p.Sections.Add(Mod("M2", "Modulo 2 - Trasporto", 13000, 14000));
        p.Sections.Add(Mod("M3", "Modulo 3 - Legatura", 27000, 16000));
        p.Sections.Add(Mod("M4", "Modulo 4 - Accumulo trasferitore", 43000, 14000));

        p.Photocells.Add(Entry());
        p.Photocells.Add(Pc("FC1", "FC1 accumulo ingresso", 11500, "M1", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC2", "FC2 accumulo", 25500, "M2", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC3", "FC3 fine legatura", 41500, "M3", PhotocellAction.Accumulation));
        p.Photocells.Add(Pc("FC4", "FC4 rallentamento stopper", 54500, "M4", PhotocellAction.SlowDown));
        p.Photocells.Add(Pc("FC5", "FC5 presenza stopper", 56300, "", PhotocellAction.None));

        p.Strappers.Add(Str("L1", "Legatrice KNCA 1", 31500));
        p.Strappers.Add(Str("L2", "Legatrice KNCA 2", 36000));
        p.Strappers.Add(Str("L3", "Legatrice KNCA 3", 40500));
        p.StrappingStation = new StrappingStationConfig
        {
            IsEnabled = true, InverterId = "M3", PositioningSpeed = 0.3, PositioningToleranceMm = 2, Strategy = StrappingStrategyMode.Automatic
        };
        // Tempi di default da documento: KNOF chiusura/apertura 2 s, compattatore 3 s
        p.BundleClamp = new BundleClampConfig { IsEnabled = true, PositionMm = 30800, HoldTimeS = 0.5 };
        p.Compactor = new SideCompactorConfig
        {
            IsEnabled = true, PositionMm = 32200, JawLengthMm = 1500, HoldTimeS = 1.0, OpenPhase = CompactorOpenPhase.AfterStrapping
        };
        // Rigettatrice generica (scarto): presente ma disabilitata; la SMA è una reggettatrice
        p.Rejector = new RejectorConfig
        {
            IsEnabled = false, PositionMm = 14500, LengthMm = 12000, Side = LateralSide.Left, StrokeMm = 1800,
            EjectTimeS = 3, ReturnTimeS = 3, RestartDelayS = 1
        };

        p.Stopper = new StopperConfig
        {
            Id = "STP", Name = "Stopper finale", PositionMm = 56500, AlignmentTimeS = 2.0, HeadingTimeS = 0.5,
            AlignmentRollerSpeed = 0.1, IsRaised = true, MaxImpactSpeed = 0.25
        };
        p.IntermediateStops.Add(new IntermediateStopConfig
        {
            Id = "FM1", Name = "Fermo intermedio", PositionMm = 50100, Reference = StopReference.Head, AlignmentTimeS = 2.0
        });
        p.Transfer = new TransferConfig
        {
            Id = "TRF", Name = "Trasferitore sollevabile", PositionMm = 44000, LengthMm = 12500, LiftStrokeMm = 150,
            TranslationStrokeMm = 3000, TranslationSpeed = 0.5, LiftTimeS = 2, LowerTimeS = 2, DepositTimeS = 1,
            BlockInfeedUntilHome = true, BatchSize = 2, BatchWaitTimeoutS = 60
        };
        // Banking e intestatura si scelgono nelle simulazioni: questo campione lavora in double banking di testa
        p.Simulation.Banking = AccumulationMode.DoubleBanking;
        p.Simulation.Heading = StopReference.Head;
        p.Scales.Add(new WeighingScaleConfig { Id = "B1", Name = "Bilico 1", StartMm = 44000, LengthMm = 6200 });
        p.Scales.Add(new WeighingScaleConfig { Id = "B2", Name = "Bilico 2", StartMm = 50300, LengthMm = 6300 });

        p.PackageTypes.Add(new PackageType { Id = "P6", Name = "Pacco 6 m", LengthMm = 6000, WeightKg = 1500, StrapPositionsMm = new List<double> { 750, 3000, 5250 } });
        p.PackageTypes.Add(new PackageType { Id = "P12", Name = "Pacco 12 m", LengthMm = 12000, WeightKg = 3000, StrapPositionsMm = new List<double> { 1500, 6000, 10500 } });
        p.PackageTypes.Add(new PackageType { Id = "P4", Name = "Pacco corto 4 m", LengthMm = 4000, WeightKg = 1000, StrapPositionsMm = new List<double> { 800, 2000, 3200 } });

        p.Infeed = new InfeedConfig
        {
            PackageCount = 8, IntervalS = 45, MinimumGapMm = 1000,
            TypeSequence = new List<string> { "P6", "P6", "P12", "P4" },
            WeightOverrides = "2=1480; 6=1525"
        };
        p.Simulation = new SimulationSettings();
        return Complete(p);
    }

    private static PhotocellConfig Entry() => new()
    {
        Id = "FC0", Name = "FC0 ingresso (identificazione)", PositionMm = ProjectNormalizer.DefaultEntryOffsetMm,
        ResponseTimeMs = 20, IsEntryPhotocell = true, Action = PhotocellAction.None
    };

    /// <summary>Modulo di via a rulli con il proprio inverter (0,15/0,6 m/s, 0,5/0,6 m/s²).</summary>
    private static RollerSection Mod(string id, string name, double start, double length) => new()
    {
        Id = id, Name = name, StartMm = start, LengthMm = length, WidthMm = 1200,
        RollerPitchMm = 300, RollerCount = (int)Math.Floor(length / 300.0) + 1 - (length % 300 == 0 ? 1 : 0),
        RollerDiameterMm = 150, LowSpeed = 0.15, HighSpeed = 0.6, Acceleration = 0.5, Deceleration = 0.6, DefaultSpeed = SpeedLevel.High
    };

    private static PhotocellConfig Pc(string id, string name, double pos, string inv, PhotocellAction action) => new()
    {
        Id = id, Name = name, PositionMm = pos, ControlledInverterId = inv, Action = action, ResponseTimeMs = 20
    };

    /// <summary>Legatrice KNCA 7/800 con i tempi di default (carica filo 3 s, ciclo di legatura 11,5 s).</summary>
    private static StrapperConfig Str(string id, string name, double pos) => new()
    {
        Id = id, Name = name, PositionMm = pos, FootprintLengthMm = 1200
    };
}
