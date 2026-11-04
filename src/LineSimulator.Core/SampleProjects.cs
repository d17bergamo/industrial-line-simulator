using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Modelli di area di legatura già configurati. Regole comuni a tutti i modelli:
/// - il modulo di legatura è sempre il primo della linea (la fotocellula di ingresso è al suo inizio);
/// - legatrici a 2500 mm l'una dall'altra, ad almeno 3 m dal trasferitore sollevabile;
/// - uno stringifascio KNOF davanti a ogni legatrice;
/// - la via a rulli davanti al trasferitore ha la stessa lunghezza del trasferitore;
/// - tipologie di pacco 6, 9, 12, 18 e 24 m; in ingresso solo quelle per cui il modello è dimensionato;
/// - ingresso: un pacco alla volta nella via rulli di legatura (vedi <see cref="InfeedMode.AfterStrapping"/>).
/// </summary>
public static class SampleProjects
{
    public static IReadOnlyList<(string Name, Func<LineProject> Factory)> All { get; } = new List<(string, Func<LineProject>)>
    {
        ("3 legatrici - pacchi 6/9/12/18/24 m", CreateThreeStrappers24m),
        ("2 legatrici - pacchi 6/9/12/18 m", CreateTwoStrappers18m),
        ("2 legatrici - pacchi 6/9/12 m", CreateTwoStrappers12m),
        ("1 legatrice - pacchi 6/9/12 m", CreateOneStrapper12m)
    };

    /// <summary>Distanza tra le legatrici in tutti i modelli.</summary>
    public const double StrapperSpacingMm = 2500;

    /// <summary>Lunghezze delle tipologie di pacco presenti in tutti i modelli (m).</summary>
    public static IReadOnlyList<int> PackageLengthsM { get; } = new[] { 6, 9, 12, 18, 24 };

    /// <summary>Modello aperto all'avvio: 3 legatrici, pacchi fino a 24 m.</summary>
    public static LineProject CreateStandardLine() => CreateThreeStrappers24m();

    public static LineProject CreateOneStrapper12m() =>
        Build("1 legatrice - pacchi 6/9/12 m", strappers: 1, maxLengthM: 12, infeedLengthsM: new[] { 6, 9, 12 });

    public static LineProject CreateTwoStrappers12m() =>
        Build("2 legatrici - pacchi 6/9/12 m", strappers: 2, maxLengthM: 12, infeedLengthsM: new[] { 6, 9, 12 });

    public static LineProject CreateTwoStrappers18m() =>
        Build("2 legatrici - pacchi 6/9/12/18 m", strappers: 2, maxLengthM: 18, infeedLengthsM: new[] { 6, 9, 12, 18 });

    public static LineProject CreateThreeStrappers24m() =>
        Build("3 legatrici - pacchi 6/9/12/18/24 m", strappers: 3, maxLengthM: 24, infeedLengthsM: new[] { 6, 9, 12, 18, 24 }, intermediateStop: true);

    /// <summary>
    /// M1 via rulli di legatura (primo modulo, lunga 2 × pacco massimo + 1 m: il pacco più lungo attraversa tutte le macchine)
    /// con le legatrici al centro a 2500 mm e un KNOF davanti a ciascuna; fotocellula di uscita legatura a fine modulo
    /// → M2 via rulli davanti al trasferitore, lunga come il trasferitore (pacco massimo + 0,5 m) con stopper finale
    /// → trasferitore sollevabile → due bilici di pesatura.
    /// </summary>
    private static LineProject Build(string name, int strappers, int maxLengthM, int[] infeedLengthsM, bool intermediateStop = false)
    {
        double lmax = maxLengthM * 1000.0;
        double strapLen = 2 * lmax + 1000;           // via rulli di legatura
        double trfLen = lmax + 500;                  // trasferitore = via rulli davanti al trasferitore
        double spread = (strappers - 1) * StrapperSpacingMm;
        double first = Math.Round(strapLen / 2 - spread / 2);

        var p = new LineProject
        {
            Name = name,
            Description = $"Area di legatura con {strappers} {(strappers == 1 ? "legatrice" : "legatrici")} KNCA a {StrapperSpacingMm:0} mm, " +
                          $"stringifascio KNOF per ogni legatrice, trasferitore sollevabile da {trfLen / 1000:0.#} m e bilici. Pacchi fino a {maxLengthM} m."
        };

        p.Sections.Add(Mod("M1", "Modulo 1 - Legatura", 0, strapLen));
        p.Sections.Add(Mod("M2", "Modulo 2 - Trasferitore", strapLen, trfLen));

        p.Photocells.Add(Entry());
        var exit = Pc("FC1", "FC1 uscita legatura", strapLen - 500, "M1", PhotocellAction.Accumulation);
        exit.Purpose = PhotocellPurpose.StrappingExit;
        p.Photocells.Add(exit);
        p.Photocells.Add(Pc("FC2", "FC2 rallentamento stopper", strapLen + trfLen - 1500, "M2", PhotocellAction.SlowDown));
        p.Photocells.Add(Pc("FC3", "FC3 presenza stopper", strapLen + trfLen - 300, "", PhotocellAction.None));

        for (int i = 0; i < strappers; i++)
            p.Strappers.Add(Str($"L{i + 1}", $"Legatrice KNCA {i + 1}", first + i * StrapperSpacingMm));
        p.StrappingStation = new StrappingStationConfig
        {
            IsEnabled = true, InverterId = "M1", PositioningSpeed = 0.3, PositioningToleranceMm = 2, Strategy = StrappingStrategyMode.Automatic
        };
        // Uno stringifascio davanti a ogni legatrice (450 mm a monte), tempi di default da documento
        p.BundleClamp = new BundleClampConfig { IsEnabled = true, PerMachine = true, MachineOffsetMm = 450, PositionMm = first - 450, HoldTimeS = 0.5 };
        p.Compactor = new SideCompactorConfig { IsEnabled = false };
        p.Rejector = new RejectorConfig { IsEnabled = false };

        p.Stopper = new StopperConfig
        {
            Id = "STP", Name = "Stopper finale", PositionMm = strapLen + trfLen - 100, DistanceFromLastRollerMm = 100,
            AlignmentTimeS = 2.0, HeadingTimeS = 0.5, AlignmentRollerSpeed = 0.1, IsRaised = true, MaxImpactSpeed = 0.25
        };
        if (intermediateStop)
            p.IntermediateStops.Add(new IntermediateStopConfig
            {
                Id = "FM1", Name = "Fermo intermedio scomparente", PositionMm = strapLen + 12000, AlignmentTimeS = 2.0
            });
        p.Transfer = new TransferConfig
        {
            Id = "TRF", Name = "Trasferitore sollevabile", Kind = TransferKind.Lifting, PositionMm = strapLen, LengthMm = trfLen,
            LiftStrokeMm = 150, TranslationStrokeMm = 3000, TranslationSpeed = 0.5, LiftTimeS = 2, LowerTimeS = 2, DepositTimeS = 1,
            BlockInfeedUntilHome = true, BatchWaitTimeoutS = 60
        };
        p.Scales.Add(new WeighingScaleConfig { Id = "B1", Name = "Bilico 1", StartMm = strapLen, LengthMm = trfLen / 2 });
        p.Scales.Add(new WeighingScaleConfig { Id = "B2", Name = "Bilico 2", StartMm = strapLen + trfLen / 2, LengthMm = trfLen / 2 });

        foreach (int m in PackageLengthsM) p.PackageTypes.Add(Package(m));

        p.Infeed = new InfeedConfig
        {
            Mode = InfeedMode.AfterStrapping, NewPackageDelayS = 10,
            PackageCount = 2 * infeedLengthsM.Length,
            TypeSequence = infeedLengthsM.Select(m => $"P{m}").ToList()
        };
        p.Simulation = new SimulationSettings { Banking = AccumulationMode.SingleBanking, Heading = StopReference.Head };
        return Complete(p);
    }

    /// <summary>Pacco da m metri: legature simmetriche a 1000 mm dalle estremità e circa ogni 3-3,5 m, a passi di 100 mm; 250 kg/m.</summary>
    public static PackageType Package(int meters)
    {
        double len = meters * 1000.0;
        int n = meters switch { <= 6 => 3, <= 9 => 4, <= 12 => 5, <= 18 => 6, _ => 7 };
        var pos = new List<double>();
        for (int i = 0; i < n; i++)
        {
            double x = 1000 + i * (len - 2000) / (n - 1);
            double r = Math.Round(x / 100.0) * 100;
            // simmetria rispetto al centro: la seconda metà è lo specchio della prima
            if (i >= (n + 1) / 2) r = len - pos[n - 1 - i];
            pos.Add(r);
        }
        return new PackageType
        {
            Id = $"P{meters}", Name = $"Pacco {meters} m", LengthMm = len, WeightKg = 250 * meters, StrapPositionsMm = pos
        };
    }

    /// <summary>Completa il modello: via a rulli davanti al trasferitore, fermo finale e sensori di processo obbligatori.</summary>
    private static LineProject Complete(LineProject p)
    {
        LayoutRules.AlignTransferModule(p);
        ProjectNormalizer.AlignStopperToLastRoller(p);
        ProjectNormalizer.EnsureProcessSensors(p);
        return p;
    }

    private static PhotocellConfig Entry() => new()
    {
        Id = "FC0", Name = "FC0 ingresso (identificazione)", PositionMm = ProjectNormalizer.DefaultEntryOffsetMm,
        ResponseTimeMs = 20, IsEntryPhotocell = true, Action = PhotocellAction.None
    };

    /// <summary>Modulo di via a rulli con il proprio inverter (0,15/0,6 m/s, 0,5/0,6 m/s²), sporgenze standard di 200 mm.</summary>
    private static RollerSection Mod(string id, string name, double start, double length)
    {
        var s = new RollerSection
        {
            Id = id, Name = name, WidthMm = 1200, RollerPitchMm = RollerSection.DefaultPitchMm,
            RollerDiameterMm = RollerSection.DefaultDiameterMm, LowSpeed = 0.15, HighSpeed = 0.6, Acceleration = 0.5, Deceleration = 0.6, DefaultSpeed = SpeedLevel.High
        };
        LineReflow.Fit(s, start, start + length);
        return s;
    }

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
