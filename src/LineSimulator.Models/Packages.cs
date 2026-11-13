using System.Text.Json.Serialization;

namespace LineSimulator.Models;

/// <summary>
/// Modalità di gestione dei pacchi sull'accumulo. Non è una caratteristica del pacco ma della simulazione
/// (<see cref="SimulationSettings.Banking"/>): lo stesso pacco si confronta in modalità diverse.
/// </summary>
public enum AccumulationMode
{
    SingleBanking = 0,
    DoubleBanking = 1
}

/// <summary>
/// Tipologia di pacco: descrizione fisica (lunghezza, larghezza, altezza, peso), posizione delle legature e
/// come il pacco si lavora sul trasferitore (banking e intestatura, nella tabella della schermata Pacchi).
/// </summary>
public sealed class PackageType : ComponentBase
{
    [UiProperty("Lunghezza pacco", "mm", "Dati", Order = 1)]
    public double LengthMm { get; set; } = 12000;

    [UiProperty("Peso pacco", "kg", "Dati", Order = 4)]
    public double WeightKg { get; set; } = 3000;

    [UiProperty("Larghezza pacco", "mm", "Dati", Order = 2)]
    public double GraphicWidthMm { get; set; } = 600;

    [UiProperty("Altezza pacco", "mm", "Dati", Order = 3)]
    public double GraphicHeightMm { get; set; } = 400;

    /// <summary>Posizioni delle legature misurate dalla testa del pacco (mm).</summary>
    [UiProperty("Posizioni delle legature dalla testa", "mm", "Legatura", Order = 24)]
    public List<double> StrapPositionsMm { get; set; } = new() { 1500, 6000, 10500 };

    /// <summary>Un solo tipo per pacco: legature a filo (KNCA) oppure reggettature a reggia d'acciaio (SMA).</summary>
    [UiProperty("Tipo di legatura (filo / reggetta)", "", "Legatura", Order = 19)]
    public BindingType BindingType { get; set; } = BindingType.Wire;

    /// <summary>
    /// Single o double banking per questo pacco (tabella della schermata Pacchi). Null = come le impostazioni della
    /// simulazione (progetti salvati prima della tabella). Le scelte possibili: <c>BankingOptions</c> nel Core.
    /// </summary>
    public AccumulationMode? Banking { get; set; }

    /// <summary>
    /// Intestatura di testa o di coda per questo pacco. Null = predefinita (di coda con il fermo scomparente all'estremità,
    /// altrimenti di testa). Senza fermo scomparente "di coda" vuol dire fermato per posizionamento con la coda a inizio trasferitore.
    /// </summary>
    public StopReference? Heading { get; set; }

    [JsonIgnore]
    [UiProperty("Numero di legature", "", "Legatura", ReadOnly = true, Order = 20)]
    public int StrapCount => StrapPositionsMm.Count;

    [JsonIgnore]
    [UiProperty("Distanze tra legature", "mm", "Legatura", ReadOnly = true, Order = 23)]
    public string StrapSpacingsText
    {
        get
        {
            var sorted = StrapPositionsMm.OrderBy(p => p).ToList();
            var gaps = new List<string>();
            for (int i = 1; i < sorted.Count; i++)
                gaps.Add((sorted[i] - sorted[i - 1]).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(" / ", gaps);
        }
    }
}

/// <summary>Regola di ingresso dei pacchi in linea.</summary>
public enum InfeedMode
{
    /// <summary>Un pacco alla volta in legatura (creazione dopo la fotocellula di ingresso, avanzamento a legatura del precedente completata).</summary>
    AfterStrapping = 0,
    /// <summary>Ingresso a intervallo fisso con distanza minima (comportamento delle versioni precedenti).</summary>
    FixedInterval = 1
}

/// <summary>Configurazione dell'ingresso pacchi in linea.</summary>
public sealed class InfeedConfig
{
    [UiProperty("Numero pacchi da simulare", "", "Ingresso", Order = 1)]
    public int PackageCount { get; set; } = 10;

    /// <summary>
    /// Simulazione continua (default): nell'app i pacchi entrano senza fine finché l'utente non ferma la simulazione;
    /// PackageCount vale solo per le misure fatte "fino alla fine" (test, ottimizzazione).
    /// </summary>
    public bool Continuous { get; set; } = true;

    /// <summary>
    /// Regola di ingresso. Predefinita: il nuovo pacco si crea quando la coda del precedente libera la fotocellula di ingresso
    /// della via rulli di legatura (+ <see cref="NewPackageDelayS"/>) e, in attesa su FC0, avanza quando il precedente ha
    /// completato l'ultimo ciclo di legatura (i due pacchi possono avanzare insieme).
    /// </summary>
    [UiProperty("Regola di ingresso pacchi", "", "Ingresso", Order = 2)]
    public InfeedMode Mode { get; set; } = InfeedMode.AfterStrapping;

    [UiProperty("Ritardo creazione nuovo pacco (dopo uscita coda dalla fotocellula di ingresso legatura)", "s", "Ingresso", Order = 3)]
    public double NewPackageDelayS { get; set; } = 10;

    /// <summary>Solo con regola "A intervallo fisso".</summary>
    [UiProperty("Intervallo di ingresso (solo a intervallo fisso)", "s", "Ingresso", Order = 7)]
    public double IntervalS { get; set; } = 30;

    [UiProperty("Distanza minima tra pacchi in ingresso (solo a intervallo fisso)", "mm", "Ingresso", Order = 8)]
    public double MinimumGapMm { get; set; } = 1000;

    [UiProperty("Istante primo ingresso", "s", "Ingresso", Order = 4)]
    public double FirstEntryTimeS { get; set; }

    /// <summary>Sequenza ciclica degli ID tipologia pacco. Vuota = prima tipologia.</summary>
    [UiProperty("Sequenza tipologie (ID)", "", "Ingresso", Order = 5)]
    public List<string> TypeSequence { get; set; } = new();

    /// <summary>Pesi specifici per pacco: "3=2950; 5=3100" (altrimenti il peso della tipologia).</summary>
    [UiProperty("Pesi per pacco (n=kg; ...)", "", "Ingresso", Order = 6)]
    public string WeightOverrides { get; set; } = string.Empty;
}
