using System.Text.Json.Serialization;

namespace LineSimulator.Models;

/// <summary>
/// Modulo di via a rulli: ogni modulo ha il proprio inverter, quindi contiene sia la geometria
/// sia i parametri di azionamento (velocità in m/s, rampe in m/s²). Un modulo = una zona di comando.
/// </summary>
public sealed class RollerSection : ComponentBase, IGroupChild
{
    [UiProperty("Inizio modulo (posizione in linea)", "mm", "Geometria", Order = 1)]
    public double StartMm { get; set; }

    /// <summary>
    /// Lunghezza del modulo (multipla di 50 mm): con il numero di rulli è l'unico dato che si imposta; il passo è lunghezza / rulli
    /// (sporgenza terminale = metà passo, vedi <see cref="RecalculateLength"/>).
    /// </summary>
    [UiProperty("Lunghezza (multipli di 50 mm)", "mm", "Geometria", Order = 2)]
    public double LengthMm { get; set; } = 7500;

    [UiProperty("Tavola rullo", "mm", "Geometria", Order = 3)]
    public double WidthMm { get; set; } = 700;

    [UiProperty("Numero rulli", "", "Geometria", Order = 4)]
    public int RollerCount { get; set; } = 6;

    [UiProperty("Passo rulli", "mm", "Geometria", Order = 5)]
    public double RollerPitchMm { get; set; } = DefaultPitchMm;

    [UiProperty("Diametro rulli (grafico)", "mm", "Geometria", Order = 6)]
    public double RollerDiameterMm { get; set; } = DefaultDiameterMm;

    /// <summary>Passo e diametro rulli di default in tutti i modelli.</summary>
    public const double DefaultPitchMm = 1250, DefaultDiameterMm = 268;

    /// <summary>Sporgenza della struttura oltre l'asse del primo/ultimo rullo, per lato: sempre metà del passo rulli.</summary>
    [JsonIgnore]
    [UiProperty("Sporgenza terminale (metà passo rulli)", "mm", "Geometria", ReadOnly = true, Order = 7)]
    public double EndOverhangMm => RollerPitchMm / 2;

    /// <summary>Distanza tra gli assi del primo e dell'ultimo rullo: L = passo × (N − 1).</summary>
    [JsonIgnore]
    [UiProperty("Lunghezza interassiale (calcolata)", "mm", "Geometria", ReadOnly = true, Order = 8)]
    public double InteraxialLengthMm => RollerPitchMm * Math.Max(0, RollerCount - 1);

    /// <summary>Ricalcola la lunghezza fisica: interasse + sporgenza a entrambe le estremità.</summary>
    public void RecalculateLength() => LengthMm = InteraxialLengthMm + 2 * EndOverhangMm;

    /// <summary>Parametri di default delle vie a rulli (m/s, m/s²).</summary>
    public const double DefaultMinSpeed = 0.3, DefaultNominalSpeed = 1.0, DefaultMaxSpeed = 1.5, DefaultAccel = 1.0;

    [UiProperty("Velocità minima", "m/s", "Inverter", Order = 20)]
    public double LowSpeed { get; set; } = DefaultMinSpeed;

    [UiProperty("Velocità nominale", "m/s", "Inverter", Order = 21)]
    public double HighSpeed { get; set; } = DefaultNominalSpeed;

    [UiProperty("Velocità massima", "m/s", "Inverter", Order = 27)]
    public double MaxSpeed { get; set; } = DefaultMaxSpeed;

    [UiProperty("Accelerazione", "m/s²", "Inverter", Order = 22)]
    public double Acceleration { get; set; } = DefaultAccel;

    [UiProperty("Decelerazione", "m/s²", "Inverter", Order = 23)]
    public double Deceleration { get; set; } = DefaultAccel;

    /// <summary>True = i parametri di questo modulo sono personalizzati e non seguono più il gruppo (padre).</summary>
    [UiProperty("Personalizza questo modulo (sblocca)", "", "Gruppo", Order = 0)]
    public bool OverrideGroup { get; set; }

    [UiProperty("Velocità di default in marcia", "", "Inverter", Order = 24)]
    public SpeedLevel DefaultSpeed { get; set; } = SpeedLevel.High;

    /// <summary>
    /// Arresto automatico: quando nel modulo e nel modulo precedente non ci sono pacchi
    /// (né pacchi in arrivo) l'inverter si ferma; riparte appena un pacco si avvicina.
    /// </summary>
    [UiProperty("Arresto automatico senza pacchi", "", "Inverter", Order = 25)]
    public bool AutoStopWhenEmpty { get; set; } = true;

    /// <summary>
    /// Zona di comando (inverter) del modulo. Vuoto = il modulo ha il proprio inverter.
    /// Moduli contigui con lo stesso valore sono comandati da un solo inverter: i parametri di azionamento
    /// sono quelli del primo modulo della zona.
    /// </summary>
    [UiProperty("Zona di comando / inverter (vuoto = proprio)", "", "Inverter", Order = 26)]
    public string CommandZoneId { get; set; } = string.Empty;

    /// <summary>
    /// Motori commutabili (svincolo): il modulo ha il suo inverter, ma i motori vengono commutati sull'inverter della via di
    /// legatura mentre il pacco in legatura lo impegna per le legature di coda (stessa velocità e stessi posizionamenti);
    /// negli altri momenti è indipendente e porta via il pacco già legato. Lo propone l'ottimizzazione della produttività.
    /// </summary>
    [UiProperty("Motori commutabili sull'inverter di legatura", "", "Inverter", Order = 27)]
    public bool FollowsStrapping { get; set; }

    /// <summary>Identificativo della zona di comando effettiva (il modulo stesso se non indicata).</summary>
    [JsonIgnore]
    public string EffectiveZoneId => string.IsNullOrWhiteSpace(CommandZoneId) ? Id : CommandZoneId.Trim();

    [UiProperty("Direzione di movimentazione", "", "Funzionamento", Order = 10)]
    public TransportDirection Direction { get; set; } = TransportDirection.Forward;

    [UiProperty("Modulo operativo (inverter abilitato)", "", "Funzionamento", Order = 12)]
    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public double EndMm => StartMm + LengthMm;

    [JsonIgnore]
    [UiProperty("Tempo di accelerazione (0→alta)", "s", "Inverter", ReadOnly = true, Order = 30)]
    public double AccelerationTimeS => Acceleration > 0 ? HighSpeed / Acceleration : double.PositiveInfinity;

    [JsonIgnore]
    [UiProperty("Tempo di frenatura (alta→0)", "s", "Inverter", ReadOnly = true, Order = 31)]
    public double BrakingTimeS => Deceleration > 0 ? HighSpeed / Deceleration : double.PositiveInfinity;

    [JsonIgnore]
    [UiProperty("Spazio di frenatura da alta", "mm", "Inverter", ReadOnly = true, Order = 32)]
    public double BrakingDistanceHighMm => Deceleration > 0 ? HighSpeed * HighSpeed / (2 * Deceleration) * 1000.0 : double.PositiveInfinity;

    /// <summary>Parametri di azionamento del modulo nella forma usata dal motore e dall'ottimizzatore.</summary>
    public InverterConfig ToDrive() => new()
    {
        Id = Id, Name = Name, LowSpeed = LowSpeed, HighSpeed = HighSpeed, Acceleration = Acceleration,
        Deceleration = Deceleration, DefaultSpeed = DefaultSpeed, IsEnabled = IsEnabled, AutoStopWhenEmpty = AutoStopWhenEmpty
    };
}

/// <summary>
/// Padre dei moduli via rulli: i suoi parametri valgono per tutti i moduli che non hanno la spunta "personalizza".
/// </summary>
public sealed class RollerGroup : ComponentBase
{
    public RollerGroup() { Id = "RLG"; Name = "Moduli via a rulli"; }

    [UiProperty("Tavola rullo", "mm", "Geometria", Order = 3)]
    public double WidthMm { get; set; } = 700;

    [UiProperty("Altezza via a rulli", "mm", "Geometria", Order = 1)]
    public double RollerTopElevationMm { get; set; } = 800;

    [UiProperty("Passo rulli (multipli di 50 mm)", "mm", "Geometria", Order = 5)]
    public double RollerPitchMm { get; set; } = RollerSection.DefaultPitchMm;

    [UiProperty("Diametro rulli (grafico)", "mm", "Geometria", Order = 6)]
    public double RollerDiameterMm { get; set; } = RollerSection.DefaultDiameterMm;

    [UiProperty("Velocità minima", "m/s", "Azionamento", Order = 20)]
    public double LowSpeed { get; set; } = RollerSection.DefaultMinSpeed;

    [UiProperty("Velocità nominale", "m/s", "Azionamento", Order = 21)]
    public double HighSpeed { get; set; } = RollerSection.DefaultNominalSpeed;

    [UiProperty("Velocità massima", "m/s", "Azionamento", Order = 22)]
    public double MaxSpeed { get; set; } = RollerSection.DefaultMaxSpeed;

    [UiProperty("Accelerazione", "m/s²", "Azionamento", Order = 23)]
    public double Acceleration { get; set; } = RollerSection.DefaultAccel;

    [UiProperty("Decelerazione", "m/s²", "Azionamento", Order = 24)]
    public double Deceleration { get; set; } = RollerSection.DefaultAccel;

    [UiProperty("Velocità di default in marcia", "", "Azionamento", Order = 25)]
    public SpeedLevel DefaultSpeed { get; set; } = SpeedLevel.High;

    [UiProperty("Arresto automatico senza pacchi", "", "Azionamento", Order = 26)]
    public bool AutoStopWhenEmpty { get; set; } = true;
}

/// <summary>
/// Parametri di azionamento (inverter) di un modulo di via a rulli. Non è più un componente separato del progetto:
/// viene ricavato da <see cref="RollerSection.ToDrive"/>.
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

public sealed class PhotocellConfig : ComponentBase, IGroupChild
{
    [UiProperty("Posizione in linea", "mm", "Geometria", Order = 1)]
    public double PositionMm { get; set; }

    /// <summary>True = i parametri di questa fotocellula sono personalizzati e non seguono la famiglia.</summary>
    [UiProperty("Personalizza questa fotocellula (sblocca)", "", "Gruppo", Order = 0)]
    public bool OverrideGroup { get; set; }

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

    /// <summary>Funzione di processo della fotocellula (arresto davanti al trasferitore, riferimento encoder di legatura...).</summary>
    [UiProperty("Scopo di processo", "", "Logica", Order = 14)]
    public PhotocellPurpose Purpose { get; set; } = PhotocellPurpose.Generic;

    [UiProperty("Aggiunta automaticamente dal sistema", "", "Logica", ReadOnly = true, Order = 15)]
    public bool IsAutoGenerated { get; set; }

    /// <summary>
    /// Fotocellula unica con due ruoli: oltre al suo scopo (es. uscita via rulli di legatura) fa anche da arresto davanti
    /// al trasferitore. Si imposta quando le due fotocellule sarebbero vicine e vengono unite.
    /// </summary>
    [UiProperty("Fa anche da arresto davanti al trasferitore", "", "Logica", Order = 16)]
    public bool AlsoTransferStop { get; set; }

    /// <summary>Vera se la fotocellula ferma il materiale davanti al trasferitore sollevato (scopo proprio o unita).</summary>
    [JsonIgnore]
    public bool ActsAsTransferStop => Purpose == PhotocellPurpose.TransferStop || AlsoTransferStop;

    [UiProperty("Modulo comandato", "", "Logica", Order = 10)]
    public string ControlledInverterId { get; set; } = string.Empty;

    [UiProperty("Condizione di attivazione", "", "Logica", Order = 11)]
    public PhotocellActivation Activation { get; set; } = PhotocellActivation.WhenOccupied;

    [UiProperty("Azione conseguente", "", "Logica", Order = 12)]
    public PhotocellAction Action { get; set; } = PhotocellAction.None;
}

/// <summary>Famiglia delle fotocellule: parametri comuni ereditati dalle fotocellule non personalizzate.</summary>
public sealed class PhotocellGroup : ComponentBase
{
    public PhotocellGroup() { Id = "FCG"; Name = "Fotocellule"; }

    [UiProperty("Tipologia", "", "Sensore", Order = 2)]
    public PhotocellType Type { get; set; } = PhotocellType.ThroughBeam;

    [UiProperty("Direzione di rilevamento", "", "Sensore", Order = 3)]
    public DetectionDirection Direction { get; set; } = DetectionDirection.Transversal;

    [UiProperty("Distanza di rilevamento (grafica)", "mm", "Sensore", Order = 4)]
    public double DetectionRangeMm { get; set; } = 1500;

    [UiProperty("Tempo di risposta", "ms", "Sensore", Order = 5)]
    public double ResponseTimeMs { get; set; } = 20;

    [UiProperty("Logica di rilevamento", "", "Logica", Order = 13)]
    public PhotocellDetectionMode DetectionMode { get; set; } = PhotocellDetectionMode.Presence;

    [UiProperty("Condizione di attivazione", "", "Logica", Order = 11)]
    public PhotocellActivation Activation { get; set; } = PhotocellActivation.WhenOccupied;
}
