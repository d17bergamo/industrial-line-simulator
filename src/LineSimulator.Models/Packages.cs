using System.Text.Json.Serialization;

namespace LineSimulator.Models;

/// <summary>
/// Tipologia di pacco. Obbligatori per i calcoli: lunghezza e peso.
/// Larghezza e altezza sono solo grafiche.
/// </summary>
public sealed class PackageType : ComponentBase
{
    [UiProperty("Lunghezza pacco", "mm", "Dati", Order = 1)]
    public double LengthMm { get; set; } = 12000;

    [UiProperty("Peso pacco", "kg", "Dati", Order = 2)]
    public double WeightKg { get; set; } = 3000;

    [UiProperty("Larghezza grafica", "mm", "Grafica", Order = 10)]
    public double GraphicWidthMm { get; set; } = 600;

    [UiProperty("Altezza grafica", "mm", "Grafica", Order = 11)]
    public double GraphicHeightMm { get; set; } = 400;

    /// <summary>Posizioni delle legature misurate dalla testa del pacco (mm).</summary>
    [UiProperty("Posizioni legature dalla testa", "mm", "Legatura", Order = 20)]
    public List<double> StrapPositionsMm { get; set; } = new() { 1500, 6000, 10500 };

    [UiProperty("Distanza minima legatura-estremità", "mm", "Legatura", Order = 21)]
    public double MinStrapDistanceFromEndsMm { get; set; } = 300;

    [UiProperty("Distanza minima tra legature", "mm", "Legatura", Order = 22)]
    public double MinDistanceBetweenStrapsMm { get; set; } = 500;

    [JsonIgnore]
    [UiProperty("Numero di legature", "", "Legatura", ReadOnly = true, Order = 23)]
    public int StrapCount => StrapPositionsMm.Count;

    [JsonIgnore]
    [UiProperty("Distanze tra legature", "mm", "Legatura", ReadOnly = true, Order = 24)]
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

/// <summary>Configurazione dell'ingresso pacchi in linea.</summary>
public sealed class InfeedConfig
{
    [UiProperty("Numero pacchi da simulare", "", "Ingresso", Order = 1)]
    public int PackageCount { get; set; } = 10;

    [UiProperty("Intervallo di ingresso", "s", "Ingresso", Order = 2)]
    public double IntervalS { get; set; } = 30;

    [UiProperty("Distanza minima tra pacchi in ingresso", "mm", "Ingresso", Order = 3)]
    public double MinimumGapMm { get; set; } = 1000;

    [UiProperty("Istante primo ingresso", "s", "Ingresso", Order = 4)]
    public double FirstEntryTimeS { get; set; }

    /// <summary>Sequenza ciclica degli ID tipologia pacco. Vuota = prima tipologia.</summary>
    [UiProperty("Sequenza tipologie (ID)", "", "Ingresso", Order = 5)]
    public List<string> TypeSequence { get; set; } = new();
}
