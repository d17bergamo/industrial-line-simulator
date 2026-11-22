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

    /// <summary>Distanza della prima legatrice dall'inizio della linea (prima fotocellula): circa 4 m in tutti i modelli.</summary>
    public const double FirstStrapperFromEntryMm = 4000;

    /// <summary>
    /// Posizione della prima legatrice: 4 m dall'inizio. Il pacco viene legato anche se la coda è ancora a monte della linea
    /// (fuori dalla simulazione, sul trasporto di alimentazione).
    /// </summary>
    public static double FirstStrapperPositionMm(int strappers, int maxLengthM) => ProjectNormalizer.DefaultEntryOffsetMm + FirstStrapperFromEntryMm;

    /// <summary>
    /// Lunghezza della via rulli di legatura in tutti gli esempi: la regola rispetto all'ultima legatrice o reggettatrice,
    /// cioè fine via = ultima macchina + due rulli (<see cref="StrapperTrack.MinRollersAfterLastStrapper"/>), arrotondata
    /// al passo dei rulli. Per i pacchi lunghi lo svincolo va sotto lo stesso inverter (StrappingAreaRules).
    /// </summary>
    public static double StrappingModuleLengthMm(int strappers, int maxLengthM)
    {
        double pitch = RollerSection.DefaultPitchMm;
        double first = FirstStrapperPositionMm(strappers, maxLengthM);
        double last = first + (Math.Max(1, strappers) - 1) * StrapperSpacingMm;
        double byRule = last + StrapperTrack.MinRollersAfterLastStrapper * pitch;
        return Math.Ceiling(byRule / pitch - 1e-9) * pitch;
    }

    /// <summary>
    /// Svincolo degli esempi: almeno 4 rulli; per i pacchi lunghi quanto basta perché, con lo svincolo sotto lo stesso
    /// inverter della legatura, la legatura di coda si possa fare sotto la prima legatrice (la via di legatura resta
    /// quella della regola: ultima legatrice + due rulli).
    /// </summary>
    public static double ReleaseModuleLengthMm(int strappers, int maxLengthM)
    {
        double pitch = RollerSection.DefaultPitchMm;
        double first = FirstStrapperPositionMm(strappers, maxLengthM);
        double rearStrap = maxLengthM * 1000.0 - PackageNaming.DefaultEndDistanceMm;
        double need = first + rearStrap + 250 - StrappingModuleLengthMm(strappers, maxLengthM);
        int rollers = Math.Max(StrappingAreaRules.ReleaseModuleRollers, (int)Math.Ceiling(need / pitch - 1e-9));
        return rollers * pitch;
    }

    /// <summary>Distanza tra le legatrici in tutti i modelli.</summary>
    public const double StrapperSpacingMm = 2500;

    /// <summary>Lunghezze delle tipologie di pacco presenti in tutti i modelli (m).</summary>
    public static IReadOnlyList<int> PackageLengthsM { get; } = new[] { 6, 9, 12, 18, 24 };

    /// <summary>Modello aperto all'avvio: 2 legatrici, pacchi 6/9/12 m.</summary>
    public static LineProject CreateStandardLine() => CreateTwoStrappers12m();

    /// <summary>
    /// Layout "da zero" per l'avvio guidato: pacchi 6/9/12 m, via di ingresso, via davanti al trasferitore, trasferitore a catene,
    /// bilico e fermo finale; nessuna legatrice (si aggiungono dalla libreria). La simulazione funziona già (pacchi non legati).
    /// </summary>
    public static LineProject CreateEmpty()
    {
        var p = CreateOneStrapper12m();
        p.Name = "Nuovo layout";
        p.Strappers.Clear();
        p.BundleClamp.IsEnabled = false;
        p.Compactor.IsEnabled = false;
        return p;
    }

    /// <summary>
    /// Layout personalizzato (versione web, 2026-11-18): stesse regole degli esempi con il numero di legatrici, il pacco più
    /// lungo e le misure dei pacchi scelti dall'utente; poi si modifica pezzo per pezzo.
    /// </summary>
    public static LineProject CreateCustom(int strappers, int maxLengthM, IEnumerable<int> packageLengthsM, bool intermediateStop)
    {
        strappers = Math.Clamp(strappers, 1, 6);
        var lengths = packageLengthsM.Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
        if (lengths.Count == 0) lengths.Add(6);
        maxLengthM = Math.Max(maxLengthM, lengths.Max());
        var p = Build($"Layout personalizzato - {strappers} {(strappers == 1 ? "legatrice" : "legatrici")}, pacchi {string.Join("/", lengths)} m",
            strappers, maxLengthM, lengths.ToArray(), intermediateStop);
        return p;
    }

    public static LineProject CreateOneStrapper12m() =>
        Build("1 legatrice - pacchi 6/9/12 m", strappers: 1, maxLengthM: 12, infeedLengthsM: new[] { 6, 9, 12 });

    public static LineProject CreateTwoStrappers12m() =>
        Build("2 legatrici - pacchi 6/9/12 m", strappers: 2, maxLengthM: 12, infeedLengthsM: new[] { 6, 9, 12 });

    public static LineProject CreateTwoStrappers18m() =>
        Build("2 legatrici - pacchi 6/9/12/18 m", strappers: 2, maxLengthM: 18, infeedLengthsM: new[] { 6, 9, 12, 18 });

    public static LineProject CreateThreeStrappers24m() =>
        Build("3 legatrici - pacchi 6/9/12/18/24 m", strappers: 3, maxLengthM: 24, infeedLengthsM: new[] { 6, 9, 12, 18, 24 }, intermediateStop: true);

    /// <summary>
    /// M1 via rulli di legatura (primo modulo) con la prima legatrice a 4 m dall'inizio, le altre a 2500 mm, un KNOF davanti a
    /// ciascuna e almeno due rulli dopo l'ultima; fotocellula di uscita legatura a fine modulo
    /// → M2 via rulli di svincolo da almeno 4 rulli (più lungo per i pacchi lunghi) (il software decide se va sotto l'inverter della legatura)
    /// → M3 via rulli davanti al trasferitore, lunga come il trasferitore (pacco massimo + 0,5 m) con fermo finale
    /// → trasferitore sollevabile → bilici di pesatura (lunghezza automatica).
    /// </summary>
    private static LineProject Build(string name, int strappers, int maxLengthM, int[] infeedLengthsM, bool intermediateStop = false)
    {
        double lmax = maxLengthM * 1000.0;
        // lunghezze multiple di 2500 mm: con passo 1250 mm i rulli sono interi e il passo resta multiplo di 50 mm
        double strapLen = StrappingModuleLengthMm(strappers, maxLengthM);                         // via rulli di legatura
        double relLen = ReleaseModuleLengthMm(strappers, maxLengthM);                               // svincolo: almeno 4 rulli
        double trfStart = strapLen + relLen;
        double trfLen = Math.Ceiling((lmax + 500) / 2500) * 2500;            // trasferitore = via rulli davanti al trasferitore
        double first = FirstStrapperPositionMm(strappers, maxLengthM);

        var p = new LineProject
        {
            Name = name,
            Description = $"Area di legatura con {strappers} {(strappers == 1 ? "legatrice" : "legatrici")} a filo a {StrapperSpacingMm:0} mm, " +
                          $"prima legatrice a 4 m dall'inizio, stringifascio per ogni legatrice, svincolo da {relLen / RollerSection.DefaultPitchMm:0} rulli, trasferitore sollevabile da {trfLen / 1000:0.#} m e bilici. Pacchi fino a {maxLengthM} m."
        };

        p.Sections.Add(Mod("M1", "Modulo 1 - Legatura", 0, strapLen));
        p.Sections.Add(Mod("M2", "Modulo 2 - Svincolo", strapLen, relLen));
        p.Sections.Add(Mod("M3", "Modulo 3 - Trasferitore", trfStart, trfLen));

        p.Photocells.Add(Entry());
        var exit = Pc("FC1", "FC1 uscita legatura", strapLen, "M1", PhotocellAction.None);
        exit.Purpose = PhotocellPurpose.StrappingExit;
        exit.Action = PhotocellAction.None;        // a fine modulo non c'è spazio di frenata: solo segnale di uscita legatura
        p.Photocells.Add(exit);
        // rallentamento ad almeno 2 m dal fermo finale (PhotocellRules.MinDistanceToTargetMm)
        p.Photocells.Add(Pc("FC2", "FC2 rallentamento fermo finale", trfStart + trfLen - PhotocellRules.MinDistanceToTargetMm, "M3", PhotocellAction.SlowDown));
        // nessuna fotocellula di presenza sul fermo finale: il pacco in battuta lo segnala il fermo e lo vede FC2

        for (int i = 0; i < strappers; i++)
            p.Strappers.Add(Str($"L{i + 1}", $"Legatrice {i + 1}", first + i * StrapperSpacingMm));
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
            // fermo finale alla fine dell'ultimo modulo: a metà passo dall'ultimo rullo
            Id = "STP", Name = "Fermo finale", PositionMm = trfStart + trfLen, DistanceFromLastRollerMm = RollerSection.DefaultPitchMm / 2,
            AlignmentTimeS = 2.0, HeadingTimeS = 0.5, AlignmentRollerSpeed = 0.1, IsRaised = true,
            MaxImpactSpeed = StopperConfig.DefaultMaxImpactSpeed   // 0,3 m/s = velocità minima dei moduli
        };
        if (intermediateStop)
            p.IntermediateStops.Add(new IntermediateStopConfig
            {
                Id = "FM1", Name = "Fermo scomparente", Placement = DevicePlacement.Center, PositionMm = trfStart + trfLen / 2, AlignmentTimeS = 2.0
            });
        p.Transfer = new TransferConfig
        {
            Id = "TRF", Name = "Trasferitore sollevabile", Kind = TransferKind.Lifting, PositionMm = trfStart, LengthMm = trfLen,
            LiftStrokeMm = 150, TranslationStrokeMm = 3000, TranslationSpeed = 0.5, LiftTimeS = 3, LowerTimeS = 3, DepositTimeS = 1,
            BlockInfeedUntilHome = true, BatchWaitTimeoutS = 60,
            ScaleAtMidStroke = true          // due prese: via rulli → bilico (metà corsa) → trasferitore d'accumulo
        };
        p.AccumulationTransfer = new AccumulationTransferConfig { IsEnabled = true, WidthMm = 5000, StepGapMm = 50, StepSpeed = 0.2 };
        // Bilici: lunghezza automatica (trasferitore / numero di bilici), vedi StrappingAreaRules.LayoutScales
        if (maxLengthM <= 12)    // modelli 6/9/12 m: un solo bilico lungo come il trasferitore
            p.Scales.Add(new WeighingScaleConfig { Id = "B1", Name = "Bilico 1", Placement = DevicePlacement.Center, StartMm = trfStart, LengthMm = trfLen });
        else
        {
            p.Scales.Add(new WeighingScaleConfig { Id = "B1", Name = "Bilico 1", Placement = DevicePlacement.Center, StartMm = trfStart, LengthMm = trfLen / 2 });
            p.Scales.Add(new WeighingScaleConfig { Id = "B2", Name = "Bilico 2", Placement = DevicePlacement.End, StartMm = trfStart + trfLen / 2, LengthMm = trfLen / 2 });
        }

        // solo le tipologie selezionabili del modello (coerenti con il nome dell'esempio)
        foreach (int m in infeedLengthsM) p.PackageTypes.Add(Package(m));

        p.Infeed = new InfeedConfig
        {
            Mode = InfeedMode.AfterStrapping, NewPackageDelayS = 10,
            PackageCount = 2 * infeedLengthsM.Length,
            TypeSequence = infeedLengthsM.Select(m => PackageNaming.IdFor(m * 1000.0, BindingType.Wire)).ToList()
        };
        p.Simulation = new SimulationSettings { Banking = AccumulationMode.SingleBanking, Heading = StopReference.Head };
        return Complete(p);
    }

    /// <summary>Pacco da m metri (filo): legature di testa e di coda a 500 mm dalle estremità, le altre equidistanti e simmetriche; 250 kg/m.</summary>
    public static PackageType Package(int meters, BindingType binding = BindingType.Wire)
    {
        double len = meters * 1000.0;
        int n = meters switch { <= 6 => 3, <= 9 => 4, <= 12 => 5, <= 18 => 6, _ => 7 };
        return new PackageType
        {
            Id = PackageNaming.IdFor(len, binding), Name = $"Pacco {meters} m", LengthMm = len, WeightKg = 250 * meters,
            BindingType = binding, StrapPositionsMm = PackageNaming.DefaultStraps(len, n)
        };
    }

    /// <summary>Completa il modello: via a rulli davanti al trasferitore, fermo finale e sensori di processo obbligatori.</summary>
    private static LineProject Complete(LineProject p)
    {
        LayoutRules.AlignTransferModule(p);
        ProjectNormalizer.AlignStopperToLastRoller(p);
        StrappingAreaRules.LayoutScales(p);
        StrappingAreaRules.PlaceStops(p);
        StrappingAreaRules.DecideReleaseInverters(p);     // svincolo con la via di legatura solo se serve ai pacchi lunghi
        ProjectNormalizer.EnsureProcessSensors(p);
        TransferEntryRule.EnsureStopPhotocells(p);        // ogni fermo scomparente con la sua fotocellula di comando
        return p;
    }

    private static PhotocellConfig Entry() => new()
    {
        Id = "FC0", Name = "FC1 (ingresso identificazione)", PositionMm = ProjectNormalizer.DefaultEntryOffsetMm,
        ResponseTimeMs = 20, IsEntryPhotocell = true, Action = PhotocellAction.None
    };

    /// <summary>Modulo di via a rulli con i parametri di default del padre (0,3 / 1,0 / 1,5 m/s, 1 m/s²); sporgenza = metà passo.</summary>
    private static RollerSection Mod(string id, string name, double start, double length)
    {
        var s = new RollerSection
        {
            Id = id, Name = name, RollerPitchMm = RollerSection.DefaultPitchMm,
            RollerDiameterMm = RollerSection.DefaultDiameterMm, DefaultSpeed = SpeedLevel.High
        };
        LineReflow.Fit(s, start, start + length);
        return s;
    }

    private static PhotocellConfig Pc(string id, string name, double pos, string inv, PhotocellAction action) => new()
    {
        Id = id, Name = name, PositionMm = pos, ControlledInverterId = inv, Action = action, ResponseTimeMs = 20,
        IsAutoGenerated = action != PhotocellAction.None      // rallentamento/arresto/accumulo: li mette il programma
    };

    /// <summary>Legatrice KNCA 7/800 con i tempi di default (carica filo 3 s, ciclo di legatura 11,5 s).</summary>
    private static StrapperConfig Str(string id, string name, double pos) => new()
    {
        Id = id, Name = name, PositionMm = pos, FootprintLengthMm = 1200
    };
}
