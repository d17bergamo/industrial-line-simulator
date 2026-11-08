namespace LineSimulator.Models;

public sealed class SimulationSettings
{
    /// <summary>
    /// Aggiunge in automatico i sensori di processo obbligatori se mancano: fotocellula di arresto davanti al
    /// trasferitore sollevabile, encoder e fotocellula di riferimento prima della prima macchina di legatura.
    /// </summary>
    [UiProperty("Aggiungi in automatico i sensori di processo mancanti", "", "Simulazione", Order = 0)]
    public bool AutoAddProcessSensors { get; set; } = true;

    /// <summary>Obiettivo di produttività in tonnellate/ora sul mix di pacchi in ingresso (0 = nessun obiettivo).</summary>
    [UiProperty("Obiettivo di produttività", "t/h", "Obiettivo", Order = 1)]
    public double TargetTonnesPerHour { get; set; }

    /// <summary>Il software decide inverter e inseguimento dello svincolo (spegnere solo per confronti o casi particolari).</summary>
    public bool AutoReleaseInverters { get; set; } = true;

    [UiProperty("Passo di integrazione", "ms", "Simulazione", Order = 1)]
    public double TimeStepMs { get; set; } = 10;

    [UiProperty("Durata massima simulazione", "s", "Simulazione", Order = 2)]
    public double MaxSimulationTimeS { get; set; } = 7200;

    [UiProperty("Intervallo registrazione fotogrammi", "ms", "Simulazione", Order = 3)]
    public double RecordIntervalMs { get; set; } = 50;

    [UiProperty("Timeout rilevamento blocco (deadlock)", "s", "Simulazione", Order = 4)]
    public double DeadlockTimeoutS { get; set; } = 120;

    [UiProperty("Distanza minima di sicurezza tra pacchi", "mm", "Sicurezza", Order = 10)]
    public double MinSafetyDistanceMm { get; set; } = 300;

    [UiProperty("Decelerazione massima per attrito pacco-rulli", "m/s²", "Fisica", Order = 11)]
    public double MaxFrictionDeceleration { get; set; } = 2.5;

    [UiProperty("Arresta la simulazione alla prima collisione", "", "Simulazione", Order = 12)]
    public bool StopOnCollision { get; set; }

    /// <summary>
    /// Single banking: un pacco alla volta sul trasferitore. Double banking: due pacchi (il primo sullo fermo finale, il secondo sul fermo scomparente).
    /// Si sceglie solo nella sezione Simulazioni (non è una caratteristica del pacco né della configurazione).
    /// </summary>
    public AccumulationMode Banking { get; set; } = AccumulationMode.SingleBanking;

    /// <summary>
    /// Intestatura di testa (contro il fermo) o di coda (il pacco oltrepassa il fermo scomparente, il fermo si solleva e il pacco torna indietro
    /// fino a battere di coda). Si sceglie solo nella sezione Simulazioni.
    /// </summary>
    public StopReference Heading { get; set; } = StopReference.Head;
}

/// <summary>Disposizione grafica globale della linea nella scena 3D.</summary>
public sealed class LayoutSettings
{
    public double OriginXMm { get; set; }

    public double OriginYMm { get; set; }

    public double RotationDeg { get; set; }

    /// <summary>Quota del piano rulli: si modifica dalle proprietà generali delle vie rulli (padre).</summary>
    public double RollerTopElevationMm { get; set; } = 800;
}

/// <summary>
/// Progetto completo: geometria, componenti, logiche, pacchi, legatrici e impostazioni.
/// È l'unità salvata nel file JSON.
/// </summary>
public sealed class LineProject
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");

    [UiProperty("Nome progetto", "", "Progetto", Order = 1)]
    public string Name { get; set; } = "Nuova linea";

    [UiProperty("Descrizione", "", "Progetto", Order = 2)]
    public string Description { get; set; } = string.Empty;

    public LayoutSettings Layout { get; set; } = new();
    /// <summary>Moduli di via a rulli (ognuno con il proprio inverter).</summary>
    public List<RollerSection> Sections { get; set; } = new();
    /// <summary>Proprietà comuni (padre) dei moduli via rulli: i moduli non personalizzati le ereditano.</summary>
    public RollerGroup SectionDefaults { get; set; } = new();
    public PhotocellGroup PhotocellDefaults { get; set; } = new();
    public StrapperGroup StrapperDefaults { get; set; } = new();
    public ScaleGroup ScaleDefaults { get; set; } = new();
    public List<PhotocellConfig> Photocells { get; set; } = new();
    public List<StrapperConfig> Strappers { get; set; } = new();
    public StrappingStationConfig StrappingStation { get; set; } = new();
    public StopperConfig Stopper { get; set; } = new() { Id = "STP", Name = "Stopper" };
    public TransferConfig Transfer { get; set; } = new() { Id = "TRF", Name = "Trasferitore" };
    /// <summary>Trasferitore d'accumulo in uscita dal trasferitore sollevabile (con il bilico a metà corsa).</summary>
    public AccumulationTransferConfig AccumulationTransfer { get; set; } = new();
    public BundleClampConfig BundleClamp { get; set; } = new();
    public SideCompactorConfig Compactor { get; set; } = new();
    public RejectorConfig Rejector { get; set; } = new();
    public EncoderConfig StrappingEncoder { get; set; } = new();
    public List<IntermediateStopConfig> IntermediateStops { get; set; } = new();
    public List<WeighingScaleConfig> Scales { get; set; } = new();
    public List<PackageType> PackageTypes { get; set; } = new();
    public InfeedConfig Infeed { get; set; } = new();
    public List<LogicRule> Rules { get; set; } = new();
    public SimulationSettings Simulation { get; set; } = new();

    public const int MaxStrappers = 3;
}
