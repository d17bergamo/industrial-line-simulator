using System.Text.Json.Serialization;

namespace LineSimulator.Models;

/// <summary>Legatrice automatica fissa: lega nel piano trasversale alla sua posizione in linea.</summary>
public sealed class StrapperConfig : ComponentBase
{
    [UiProperty("Modello", "", "Funzionamento", Order = 19)]
    public string Model { get; set; } = "Sund Birsta KNCA 7/800";

    [UiProperty("Posizione piano di legatura", "mm", "Geometria", Order = 1)]
    public double PositionMm { get; set; }

    [UiProperty("Ingombro lungo la linea", "mm", "Geometria", Order = 2)]
    public double FootprintLengthMm { get; set; } = 1200;

    [UiProperty("Lato macchina", "", "Geometria", Order = 3)]
    public LateralSide Side { get; set; } = LateralSide.Left;

    [UiProperty("Tempo di preparazione", "s", "Tempi", Order = 10)]
    public double PreparationTimeS { get; set; } = 2.0;

    [UiProperty("Tempo di legatura", "s", "Tempi", Order = 11)]
    public double StrappingTimeS { get; set; } = 8.0;

    [UiProperty("Tempo di ritorno", "s", "Tempi", Order = 12)]
    public double ReturnTimeS { get; set; } = 2.0;

    [UiProperty("Legatrice abilitata", "", "Funzionamento", Order = 20)]
    public bool IsEnabled { get; set; } = true;

    [UiProperty("Può lavorare in combinazione", "", "Funzionamento", Order = 21)]
    public bool AllowCombinedWork { get; set; } = true;

    [UiProperty("Allarme se attesa consenso stringifascio oltre", "s", "Interblocchi", Order = 30)]
    public double ConsentTimeoutS { get; set; } = 30;

    [UiProperty("Guasto simulato al ciclo n. (0 = nessuno)", "", "Diagnostica", Order = 40)]
    public int SimulatedFaultAtCycle { get; set; }

    [UiProperty("Tempo di ripristino dopo guasto", "s", "Diagnostica", Order = 41)]
    public double FaultResetTimeS { get; set; } = 30;

    [JsonIgnore]
    [UiProperty("Tempo ciclo legatrice", "s", "Tempi", ReadOnly = true, Order = 13)]
    public double CycleTimeS => PreparationTimeS + StrappingTimeS + ReturnTimeS;
}

public enum StrappingStrategyMode
{
    /// <summary>Ottimizzazione esatta su tutte le combinazioni ammissibili.</summary>
    Automatic = 0,
    /// <summary>Piano inserito dall'utente.</summary>
    Manual = 1,
    /// <summary>Strategia predefinita: prima legatrice abilitata, legature in sequenza.</summary>
    Default = 2
}

/// <summary>Assegnazione di una legatura (indice nel pattern) a una legatrice.</summary>
public sealed class StrapAssignment
{
    public string StrapperId { get; set; } = string.Empty;
    public int StrapIndex { get; set; }
}

/// <summary>Una fermata del pacco: le assegnazioni vengono eseguite in parallelo.</summary>
public sealed class ManualStrappingStop
{
    public List<StrapAssignment> Assignments { get; set; } = new();
}

public sealed class ManualStrappingPlan
{
    public string PackageTypeId { get; set; } = string.Empty;
    public List<ManualStrappingStop> Stops { get; set; } = new();
}

/// <summary>Stazione di legatura: zona inverter con posizionamento del pacco sotto le legatrici.</summary>
public sealed class StrappingStationConfig
{
    [UiProperty("Stazione di legatura attiva", "", "Legatura", Order = 1)]
    public bool IsEnabled { get; set; } = true;

    [UiProperty("Modulo zona di legatura", "", "Legatura", Order = 2)]
    public string InverterId { get; set; } = string.Empty;

    [UiProperty("Velocità di posizionamento", "m/s", "Legatura", Order = 3)]
    public double PositioningSpeed { get; set; } = 0.3;

    [UiProperty("Tolleranza di posizionamento", "mm", "Legatura", Order = 4)]
    public double PositioningToleranceMm { get; set; } = 2;

    [UiProperty("Strategia", "", "Legatura", Order = 5)]
    public StrappingStrategyMode Strategy { get; set; } = StrappingStrategyMode.Automatic;

    public List<ManualStrappingPlan> ManualPlans { get; set; } = new();

    /// <summary>Piani manuali in forma testuale: "P12: L3#1 ; L2#2 + L1#3".</summary>
    [JsonIgnore]
    [UiProperty("Piano manuale (TIPO: Lx#n ; Ly#m + Lz#k)", "", "Legatura", Order = 6)]
    public string ManualPlansText
    {
        get => ManualPlanText.Format(ManualPlans);
        set => ManualPlans = ManualPlanText.Parse(value);
    }
}

/// <summary>Stopper di fine via a rulli con allineamento/intestatura.</summary>
public sealed class StopperConfig : ComponentBase
{
    [UiProperty("Posizione battuta", "mm", "Geometria", Order = 1)]
    public double PositionMm { get; set; }

    [UiProperty("Altezza grafica", "mm", "Geometria", Order = 2)]
    public double GraphicHeightMm { get; set; } = 300;

    [UiProperty("Tempo di allineamento", "s", "Tempi", Order = 10)]
    public double AlignmentTimeS { get; set; } = 2.0;

    [UiProperty("Tempo di intestatura", "s", "Tempi", Order = 11)]
    public double HeadingTimeS { get; set; } = 0.0;

    [UiProperty("Velocità rulli in allineamento", "m/s", "Funzionamento", Order = 12)]
    public double AlignmentRollerSpeed { get; set; } = 0.1;

    [UiProperty("Stopper sollevato", "", "Funzionamento", Order = 13)]
    public bool IsRaised { get; set; } = true;

    [UiProperty("Velocità massima d'impatto ammessa", "m/s", "Sicurezza", Order = 20)]
    public double MaxImpactSpeed { get; set; } = 0.25;
}

/// <summary>Trasferitore trasversale a valle dello stopper.</summary>
/// <summary>Sezione del trasferitore sollevabile.</summary>
public sealed class TransferSection
{
    public string Name { get; set; } = "Sezione";
    public double LengthMm { get; set; } = 6000;
}

public sealed class TransferConfig : ComponentBase
{
    [UiProperty("Inizio trasferitore (in linea)", "mm", "Geometria", Order = 1)]
    public double PositionMm { get; set; }

    /// <summary>Lunghezza complessiva: somma delle sezioni (si modifica dall'elenco sezioni).</summary>
    [UiProperty("Lunghezza totale (somma delle sezioni)", "mm", "Geometria", ReadOnly = true, Order = 2)]
    public double LengthMm { get; set; } = 12000;

    /// <summary>Sezioni del trasferitore sollevabile (numero, nome, lunghezza). Vuoto = una sezione unica lunga <see cref="LengthMm"/>.</summary>
    public List<TransferSection> Sections { get; set; } = new();

    /// <summary>Elenco effettivo delle sezioni: se non configurate, una sezione unica.</summary>
    public IReadOnlyList<TransferSection> EffectiveSections() =>
        Sections.Count > 0 ? Sections : new List<TransferSection> { new() { Name = "Sezione 1", LengthMm = LengthMm } };

    /// <summary>Imposta le sezioni e aggiorna la lunghezza complessiva.</summary>
    public void SetSections(IEnumerable<TransferSection> sections)
    {
        Sections = sections.Select(s => new TransferSection { Name = s.Name, LengthMm = s.LengthMm }).ToList();
        if (Sections.Count > 0) LengthMm = Sections.Sum(s => s.LengthMm);
    }

    [UiProperty("Larghezza grafica", "mm", "Geometria", Order = 3)]
    public double WidthMm { get; set; } = 400;

    [UiProperty("Corsa di sollevamento", "mm", "Geometria", Order = 4)]
    public double LiftStrokeMm { get; set; } = 150;

    [UiProperty("Corsa di traslazione", "mm", "Geometria", Order = 5)]
    public double TranslationStrokeMm { get; set; } = 3000;

    [UiProperty("Lato di destinazione", "", "Geometria", Order = 6)]
    public LateralSide DestinationSide { get; set; } = LateralSide.Right;

    [UiProperty("Tempo di sollevamento", "s", "Tempi", Order = 10)]
    public double LiftTimeS { get; set; } = 2.0;

    [UiProperty("Tempo di abbassamento", "s", "Tempi", Order = 11)]
    public double LowerTimeS { get; set; } = 2.0;

    [UiProperty("Velocità di traslazione", "m/s", "Tempi", Order = 12)]
    public double TranslationSpeed { get; set; } = 0.5;

    [UiProperty("Tempo di traslazione imposto (0 = da corsa/velocità)", "s", "Tempi", Order = 13)]
    public double TranslationTimeOverrideS { get; set; }

    [UiProperty("Tempo di deposito", "s", "Tempi", Order = 14)]
    public double DepositTimeS { get; set; } = 1.0;

    [UiProperty("Blocca ingresso finché non torna in posizione", "", "Interblocchi", Order = 20)]
    public bool BlockInfeedUntilHome { get; set; } = true;

    [UiProperty("Pacchi prelevati per ciclo (max)", "", "Funzionamento", Order = 21)]
    public int BatchSize { get; set; } = 1;

    [UiProperty("Attesa massima per completare il gruppo", "s", "Funzionamento", Order = 22)]
    public double BatchWaitTimeoutS { get; set; } = 60;

    [JsonIgnore]
    [UiProperty("Tempo di traslazione effettivo", "s", "Tempi", ReadOnly = true, Order = 15)]
    public double EffectiveTranslationTimeS =>
        TranslationTimeOverrideS > 0 ? TranslationTimeOverrideS
        : TranslationSpeed > 0 ? TranslationStrokeMm / 1000.0 / TranslationSpeed : double.PositiveInfinity;

    [JsonIgnore]
    [UiProperty("Ciclo completo trasferitore", "s", "Tempi", ReadOnly = true, Order = 16)]
    public double FullCycleTimeS => LiftTimeS + EffectiveTranslationTimeS + LowerTimeS + DepositTimeS + EffectiveTranslationTimeS;

    [JsonIgnore]
    public double EndMm => PositionMm + LengthMm;
}
