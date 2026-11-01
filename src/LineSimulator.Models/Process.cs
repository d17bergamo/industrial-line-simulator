using System.Globalization;
using System.Text.Json.Serialization;

namespace LineSimulator.Models;

/// <summary>Logica di rilevamento della fotocellula.</summary>
public enum PhotocellDetectionMode
{
    /// <summary>Presenza: uscita attiva finché il raggio è interrotto.</summary>
    Presence = 0,
    /// <summary>Arrivo: impulso di una scansione sul fronte di salita (testa del pacco).</summary>
    ArrivalPulse = 1,
    /// <summary>Uscita completa: impulso di una scansione sul fronte di discesa (coda del pacco passata).</summary>
    ClearancePulse = 2
}

/// <summary>
/// Base dei dispositivi con sequenza automatica: abilitazione, consenso di avvio, condizione di arresto,
/// conferme con ritardo e timeout, guasto simulabile e ripristino. Le condizioni usano la sintassi
/// delle regole logiche (vedi <see cref="ConditionText"/>); stringa vuota = sempre soddisfatta / mai attiva.
/// </summary>
public abstract class SequenceDeviceConfig : ComponentBase
{
    [UiProperty("Dispositivo abilitato", "", "Funzionamento", Order = 1)]
    public bool IsEnabled { get; set; }

    [UiProperty("Modello", "", "Funzionamento", Order = 2)]
    public string Model { get; set; } = string.Empty;

    [UiProperty("Consenso di avvio (condizione; vuoto = sempre)", "", "Interblocchi", Order = 50)]
    public string StartConsent { get; set; } = string.Empty;

    [UiProperty("Condizione di arresto (condizione; vuoto = nessuna)", "", "Interblocchi", Order = 51)]
    public string StopCondition { get; set; } = string.Empty;

    [UiProperty("Ritardo segnale di conferma", "s", "Interblocchi", Order = 52)]
    public double ConfirmDelayS { get; set; } = 0.2;

    [UiProperty("Timeout conferma", "s", "Interblocchi", Order = 53)]
    public double ConfirmTimeoutS { get; set; } = 10;

    [UiProperty("Guasto simulato al ciclo n. (0 = nessuno)", "", "Diagnostica", Order = 60)]
    public int SimulatedFaultAtCycle { get; set; }

    [UiProperty("Tempo di ripristino dopo guasto", "s", "Diagnostica", Order = 61)]
    public double FaultResetTimeS { get; set; } = 30;
}

/// <summary>Stringifascio (Sund Birsta KNOF) in ingresso alla legatrice: chiude il fascio durante la legatura.</summary>
public sealed class BundleClampConfig : SequenceDeviceConfig
{
    public BundleClampConfig()
    {
        Id = "KNOF";
        Name = "Stringifascio KNOF";
        Model = "Sund Birsta KNOF";
    }

    [UiProperty("Posizione in linea", "mm", "Geometria", Order = 10)]
    public double PositionMm { get; set; }

    [UiProperty("Tempo di chiusura", "s", "Tempi", Order = 20)]
    public double CloseTimeS { get; set; } = 1.5;

    [UiProperty("Tempo di mantenimento dopo la legatura", "s", "Tempi", Order = 21)]
    public double HoldTimeS { get; set; } = 0.5;

    [UiProperty("Tempo di apertura", "s", "Tempi", Order = 22)]
    public double OpenTimeS { get; set; } = 1.5;
}

/// <summary>Fase in cui il compattatore laterale si riapre.</summary>
public enum CompactorOpenPhase
{
    /// <summary>A fine legatura (in parallelo all'apertura dello stringifascio).</summary>
    AfterStrapping = 0,
    /// <summary>Appena lo stringifascio ha confermato la chiusura (il fascio è trattenuto dal KNOF).</summary>
    AfterBundleClampClosed = 1,
    /// <summary>Dopo l'apertura confermata dello stringifascio.</summary>
    AfterBundleClampOpened = 2
}

/// <summary>Compattatore laterale: compatta il pacco prima della legatura.</summary>
public sealed class SideCompactorConfig : SequenceDeviceConfig
{
    public SideCompactorConfig()
    {
        Id = "CMP";
        Name = "Compattatore laterale";
        Model = "Compattatore laterale";
    }

    [UiProperty("Posizione (centro ganasce)", "mm", "Geometria", Order = 10)]
    public double PositionMm { get; set; }

    [UiProperty("Lunghezza ganasce", "mm", "Geometria", Order = 11)]
    public double JawLengthMm { get; set; } = 1500;

    [UiProperty("Tempo di chiusura", "s", "Tempi", Order = 20)]
    public double CloseTimeS { get; set; } = 2.0;

    [UiProperty("Tempo di mantenimento prima del consenso", "s", "Tempi", Order = 21)]
    public double HoldTimeS { get; set; } = 1.0;

    [UiProperty("Tempo di apertura", "s", "Tempi", Order = 22)]
    public double OpenTimeS { get; set; } = 1.5;

    [UiProperty("Fase di apertura", "", "Funzionamento", Order = 23)]
    public CompactorOpenPhase OpenPhase { get; set; } = CompactorOpenPhase.AfterStrapping;
}

/// <summary>Rigettatrice (Sund Birsta SMA): espelle lateralmente i pacchi da scartare.</summary>
public sealed class RejectorConfig : SequenceDeviceConfig
{
    public RejectorConfig()
    {
        Id = "SMA";
        Name = "Rigettatrice SMA";
        Model = "Sund Birsta SMA";
        ConfirmTimeoutS = 8;
    }

    [UiProperty("Inizio campo di spinta (in linea)", "mm", "Geometria", Order = 10)]
    public double PositionMm { get; set; }

    [UiProperty("Lunghezza campo di spinta", "mm", "Geometria", Order = 11)]
    public double LengthMm { get; set; } = 12000;

    [UiProperty("Lato di espulsione", "", "Geometria", Order = 12)]
    public LateralSide Side { get; set; } = LateralSide.Left;

    [UiProperty("Corsa di espulsione", "mm", "Geometria", Order = 13)]
    public double StrokeMm { get; set; } = 1800;

    [UiProperty("Tolleranza di posizionamento", "mm", "Funzionamento", Order = 20)]
    public double PositionToleranceMm { get; set; } = 5;

    [UiProperty("Pacchi da rigettare (numeri progressivi, es. 3; 7)", "", "Rigetto", Order = 30)]
    public string RejectPackageNumbers { get; set; } = string.Empty;

    [UiProperty("Tipologie da rigettare (ID)", "", "Rigetto", Order = 31)]
    public List<string> RejectTypeIds { get; set; } = new();

    [UiProperty("Tempo di espulsione", "s", "Tempi", Order = 40)]
    public double EjectTimeS { get; set; } = 3.0;

    [UiProperty("Tempo di ritorno", "s", "Tempi", Order = 41)]
    public double ReturnTimeS { get; set; } = 3.0;

    [UiProperty("Ritardo consenso ripartenza linea", "s", "Tempi", Order = 42)]
    public double RestartDelayS { get; set; } = 1.0;

    /// <summary>Numeri progressivi dei pacchi da rigettare (errore di formato → eccezione).</summary>
    public IReadOnlyList<int> ParseRejectNumbers() => ProcessText.ParseIntList(RejectPackageNumbers);
}

/// <summary>Riferimento di intestatura di un fermo.</summary>
public enum StopReference
{
    /// <summary>Intestatura di testa: la testa del pacco va in battuta sul fermo.</summary>
    Head = 0,
    /// <summary>Intestatura di coda: la coda del pacco viene portata sul riferimento del fermo.</summary>
    Tail = 1
}

/// <summary>Fermo intermedio nella zona dello stopper (all'ingresso o al centro del trasferitore, o altrove).</summary>
public sealed class IntermediateStopConfig : ComponentBase
{
    [UiProperty("Posizione riferimento", "mm", "Geometria", Order = 1)]
    public double PositionMm { get; set; }

    [UiProperty("Altezza grafica", "mm", "Geometria", Order = 2)]
    public double GraphicHeightMm { get; set; } = 250;

    [UiProperty("Intestatura", "", "Funzionamento", Order = 10)]
    public StopReference Reference { get; set; } = StopReference.Head;

    [UiProperty("Fermo abilitato", "", "Funzionamento", Order = 11)]
    public bool IsEnabled { get; set; } = true;

    [UiProperty("Tempo di intestatura", "s", "Tempi", Order = 20)]
    public double AlignmentTimeS { get; set; } = 2.0;

    [UiProperty("Tolleranza di posizionamento", "mm", "Funzionamento", Order = 21)]
    public double PositionToleranceMm { get; set; } = 3;
}

/// <summary>Bilico di pesatura sul lato di deposito del trasferitore.</summary>
public sealed class WeighingScaleConfig : SequenceDeviceConfig
{
    public WeighingScaleConfig()
    {
        IsEnabled = true;
        Model = "Bilico a rulli";
        ConfirmTimeoutS = 15;
    }

    [UiProperty("Inizio bilico (in linea)", "mm", "Geometria", Order = 10)]
    public double StartMm { get; set; }

    [UiProperty("Lunghezza bilico", "mm", "Geometria", Order = 11)]
    public double LengthMm { get; set; } = 6500;

    [UiProperty("Posizione testa standard (0 = fine bilico)", "mm", "Posizionamento", Order = 20)]
    public double StandardHeadPositionMm { get; set; }

    [UiProperty("Lunghezza massima pacco corto", "mm", "Posizionamento", Order = 21)]
    public double ShortPackageMaxLengthMm { get; set; } = 4500;

    [UiProperty("Posizione testa intermedia per pacchi corti (0 = centrato)", "mm", "Posizionamento", Order = 22)]
    public double IntermediateHeadPositionMm { get; set; }

    [UiProperty("Velocità di posizionamento", "m/s", "Posizionamento", Order = 23)]
    public double PositioningSpeed { get; set; } = 0.2;

    [UiProperty("Accelerazione rulli bilico", "m/s²", "Posizionamento", Order = 24)]
    public double Acceleration { get; set; } = 0.3;

    [UiProperty("Tolleranza di posizionamento", "mm", "Posizionamento", Order = 25)]
    public double PositionToleranceMm { get; set; } = 10;

    [UiProperty("Tempo di stabilizzazione", "s", "Tempi", Order = 30)]
    public double StabilizationTimeS { get; set; } = 3.0;

    [UiProperty("Tempo di acquisizione peso", "s", "Tempi", Order = 31)]
    public double AcquisitionTimeS { get; set; } = 1.0;

    [UiProperty("Tempo di scarico dal bilico", "s", "Tempi", Order = 32)]
    public double ReleaseTimeS { get; set; } = 4.0;

    [UiProperty("Risoluzione", "kg", "Pesatura", Order = 40)]
    public double ResolutionKg { get; set; } = 2;

    [UiProperty("Portata massima", "kg", "Pesatura", Order = 41)]
    public double CapacityKg { get; set; } = 10000;

    [JsonIgnore]
    public double EndMm => StartMm + LengthMm;
}

/// <summary>Utilità di analisi dei campi testuali dei dispositivi.</summary>
public static class ProcessText
{
    public static List<int> ParseIntList(string? text)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in text.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                throw new FormatException($"'{part}' non è un numero di pacco valido (intero ≥ 1).");
            result.Add(n);
        }
        return result;
    }

    /// <summary>Formato "n=peso; m=peso" (pesi in kg).</summary>
    public static Dictionary<int, double> ParseWeights(string? text)
    {
        var result = new Dictionary<int, double>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=');
            if (kv.Length != 2
                || !int.TryParse(kv[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1
                || !double.TryParse(kv[1].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) || w <= 0)
                throw new FormatException($"'{part}' non valido: usare numeroPacco=peso, es. 3=2950.");
            result[n] = w;
        }
        return result;
    }
}
