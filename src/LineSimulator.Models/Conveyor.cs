using System.Text.Json.Serialization;

namespace LineSimulator.Models;

/// <summary>Sezione di via a rulli. Le grandezze cinematiche appartengono all'inverter associato.</summary>
public sealed class RollerSection : ComponentBase
{
    [UiProperty("Inizio sezione (posizione in linea)", "mm", "Geometria", Order = 1)]
    public double StartMm { get; set; }

    [UiProperty("Lunghezza", "mm", "Geometria", Order = 2)]
    public double LengthMm { get; set; } = 6000;

    [UiProperty("Larghezza grafica", "mm", "Geometria", Order = 3)]
    public double WidthMm { get; set; } = 1200;

    [UiProperty("Numero rulli", "", "Geometria", Order = 4)]
    public int RollerCount { get; set; } = 21;

    [UiProperty("Passo rulli", "mm", "Geometria", Order = 5)]
    public double RollerPitchMm { get; set; } = 300;

    [UiProperty("Diametro rulli (grafico)", "mm", "Geometria", Order = 6)]
    public double RollerDiameterMm { get; set; } = 150;

    [UiProperty("Direzione di movimentazione", "", "Funzionamento", Order = 10)]
    public TransportDirection Direction { get; set; } = TransportDirection.Forward;

    [UiProperty("Inverter associato", "", "Funzionamento", Order = 11)]
    public string InverterId { get; set; } = string.Empty;

    [UiProperty("Sezione operativa", "", "Funzionamento", Order = 12)]
    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public double EndMm => StartMm + LengthMm;
}

/// <summary>
/// Inverter (azionamento) che comanda una o più sezioni a rulli.
/// Velocità in m/s, accelerazioni in m/s².
/// </summary>
public sealed class InverterConfig : ComponentBase
{
    [UiProperty("Velocità bassa", "m/s", "Velocità", Order = 1)]
    public double LowSpeed { get; set; } = 0.15;

    [UiProperty("Velocità alta", "m/s", "Velocità", Order = 2)]
    public double HighSpeed { get; set; } = 0.6;

    [UiProperty("Accelerazione", "m/s²", "Rampe", Order = 3)]
    public double Acceleration { get; set; } = 0.5;

    [UiProperty("Decelerazione", "m/s²", "Rampe", Order = 4)]
    public double Deceleration { get; set; } = 0.6;

    [UiProperty("Velocità di default in marcia", "", "Velocità", Order = 5)]
    public SpeedLevel DefaultSpeed { get; set; } = SpeedLevel.High;

    [UiProperty("Inverter abilitato", "", "Funzionamento", Order = 6)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Arresto automatico: quando nella zona e nella sezione precedente non ci sono pacchi
    /// (né pacchi in arrivo) l'inverter si ferma; riparte appena un pacco si avvicina.
    /// </summary>
    [UiProperty("Arresto automatico senza pacchi", "", "Funzionamento", Order = 7)]
    public bool AutoStopWhenEmpty { get; set; } = true;

    [JsonIgnore]
    [UiProperty("Tempo di accelerazione (0→alta)", "s", "Rampe", ReadOnly = true, Order = 20)]
    public double AccelerationTimeS => Acceleration > 0 ? HighSpeed / Acceleration : double.PositiveInfinity;

    [JsonIgnore]
    [UiProperty("Tempo di frenatura (alta→0)", "s", "Rampe", ReadOnly = true, Order = 21)]
    public double BrakingTimeS => Deceleration > 0 ? HighSpeed / Deceleration : double.PositiveInfinity;

    [JsonIgnore]
    [UiProperty("Spazio di frenatura da alta", "mm", "Rampe", ReadOnly = true, Order = 22)]
    public double BrakingDistanceHighMm => Deceleration > 0 ? HighSpeed * HighSpeed / (2 * Deceleration) * 1000.0 : double.PositiveInfinity;
}

public enum PhotocellType
{
    ThroughBeam = 0,
    Retroreflective = 1,
    Diffuse = 2
}

public enum DetectionDirection
{
    Transversal = 0,
    Vertical = 1
}

/// <summary>Funzione integrata della fotocellula sull'inverter comandato.</summary>
public enum PhotocellAction
{
    /// <summary>Solo segnale: usabile nelle regole logiche.</summary>
    None = 0,
    /// <summary>Accumulo: ferma l'inverter se attiva E la zona a valle non accetta pacchi.</summary>
    Accumulation = 1,
    /// <summary>Forza la velocità bassa sull'inverter comandato mentre la condizione è vera.</summary>
    SlowDown = 2,
    /// <summary>Forza la velocità alta sull'inverter comandato mentre la condizione è vera.</summary>
    SpeedUp = 3,
    /// <summary>Arresta l'inverter comandato mentre la condizione è vera (interblocco).</summary>
    Stop = 4
}

public enum PhotocellActivation
{
    WhenOccupied = 0,
    WhenFree = 1
}

public sealed class PhotocellConfig : ComponentBase
{
    [UiProperty("Posizione in linea", "mm", "Geometria", Order = 1)]
    public double PositionMm { get; set; }

    [UiProperty("Tipologia", "", "Sensore", Order = 2)]
    public PhotocellType Type { get; set; } = PhotocellType.ThroughBeam;

    [UiProperty("Direzione di rilevamento", "", "Sensore", Order = 3)]
    public DetectionDirection Direction { get; set; } = DetectionDirection.Transversal;

    [UiProperty("Distanza di rilevamento (grafica)", "mm", "Sensore", Order = 4)]
    public double DetectionRangeMm { get; set; } = 1500;

    [UiProperty("Tempo di risposta", "ms", "Sensore", Order = 5)]
    public double ResponseTimeMs { get; set; } = 20;

    [UiProperty("Attiva", "", "Sensore", Order = 6)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>Fotocellula di ingresso: identifica ogni pacco (obbligatoria, almeno una).</summary>
    [UiProperty("Fotocellula di ingresso (identificazione pacchi)", "", "Sensore", Order = 7)]
    public bool IsEntryPhotocell { get; set; }

    [UiProperty("Logica di rilevamento", "", "Logica", Order = 13)]
    public PhotocellDetectionMode DetectionMode { get; set; } = PhotocellDetectionMode.Presence;

    [UiProperty("Inverter comandato", "", "Logica", Order = 10)]
    public string ControlledInverterId { get; set; } = string.Empty;

    [UiProperty("Condizione di attivazione", "", "Logica", Order = 11)]
    public PhotocellActivation Activation { get; set; } = PhotocellActivation.WhenOccupied;

    [UiProperty("Azione conseguente", "", "Logica", Order = 12)]
    public PhotocellAction Action { get; set; } = PhotocellAction.None;
}
