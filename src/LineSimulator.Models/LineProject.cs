namespace LineSimulator.Models;

public sealed class SimulationSettings
{
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
}

/// <summary>Disposizione grafica globale della linea nella scena 3D.</summary>
public sealed class LayoutSettings
{
    [UiProperty("Origine X scena", "mm", "Layout", Order = 1)]
    public double OriginXMm { get; set; }

    [UiProperty("Origine Y scena", "mm", "Layout", Order = 2)]
    public double OriginYMm { get; set; }

    [UiProperty("Rotazione linea", "°", "Layout", Order = 3)]
    public double RotationDeg { get; set; }

    [UiProperty("Quota piano rulli", "mm", "Layout", Order = 4)]
    public double RollerTopElevationMm { get; set; } = 800;
}

/// <summary>
/// Progetto completo: geometria, componenti, logiche, pacchi, legatrici e impostazioni.
/// È l'unità salvata nel file JSON.
/// </summary>
public sealed class LineProject
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");

    [UiProperty("Nome progetto", "", "Progetto", Order = 1)]
    public string Name { get; set; } = "Nuova linea";

    [UiProperty("Descrizione", "", "Progetto", Order = 2)]
    public string Description { get; set; } = string.Empty;

    public LayoutSettings Layout { get; set; } = new();
    public List<InverterConfig> Inverters { get; set; } = new();
    public List<RollerSection> Sections { get; set; } = new();
    public List<PhotocellConfig> Photocells { get; set; } = new();
    public List<StrapperConfig> Strappers { get; set; } = new();
    public StrappingStationConfig StrappingStation { get; set; } = new();
    public StopperConfig Stopper { get; set; } = new() { Id = "STP", Name = "Stopper" };
    public TransferConfig Transfer { get; set; } = new() { Id = "TRF", Name = "Trasferitore" };
    public List<PackageType> PackageTypes { get; set; } = new();
    public InfeedConfig Infeed { get; set; } = new();
    public List<LogicRule> Rules { get; set; } = new();
    public SimulationSettings Simulation { get; set; } = new();

    public const int MaxStrappers = 3;
}
