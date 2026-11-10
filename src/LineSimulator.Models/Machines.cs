using System.Text.Json.Serialization;

namespace LineSimulator.Models;

/// <summary>
/// Macchina di legatura fissa: legatrice a filo KNCA 7/800 o reggettatrice a reggia SMA.
/// Lega nel piano trasversale alla sua posizione in linea. Ciclo: la macchina si abbassa, lega e si rialza
/// (tempo di legatura); prima di ogni ciclo deve essere armata (carica del filo / riarmo).
/// </summary>
public sealed class StrapperConfig : ComponentBase, IGroupChild
{
    public const double KncaStrappingTimeS = 11.5, KncaArmingTimeS = 6.5, SmaStrappingTimeS = 10.0, SmaArmingTimeS = 5.0;

    private StrapperMachineType _machineType = StrapperMachineType.KNCA;

    /// <summary>
    /// Tipo di macchina. Passando da un tipo all'altro con i tempi di default, vengono proposti i default del nuovo tipo
    /// (KNCA: legatura 11,5 s, carica filo 3 s; SMA: legatura 10 s, riarmo 5 s).
    /// </summary>
    [UiProperty("Tipo di macchina (legatrice a filo / reggettatrice)", "", "Funzionamento", Order = 18)]
    public StrapperMachineType MachineType
    {
        get => _machineType;
        set
        {
            if (value == _machineType) return;
            bool defaults = _machineType == StrapperMachineType.KNCA
                ? StrappingTimeS == KncaStrappingTimeS && ArmingTimeS == KncaArmingTimeS
                : StrappingTimeS == SmaStrappingTimeS && ArmingTimeS == SmaArmingTimeS;
            _machineType = value;
            if (!defaults) return;
            bool sma = value == StrapperMachineType.SMA;
            StrappingTimeS = sma ? SmaStrappingTimeS : KncaStrappingTimeS;
            ArmingTimeS = sma ? SmaArmingTimeS : KncaArmingTimeS;
            Model = sma ? "Reggettatrice" : "Legatrice a filo";
        }
    }

    [UiProperty("Modello", "", "Funzionamento", Order = 19)]
    public string Model { get; set; } = "Legatrice a filo";

    [UiProperty("Posizione piano di legatura", "mm", "Geometria", Order = 1)]
    public double PositionMm { get; set; }

    /// <summary>True = i parametri di questa legatrice sono personalizzati e non seguono la famiglia.</summary>
    [UiProperty("Personalizza questa legatrice (sblocca)", "", "Gruppo", Order = 0)]
    public bool OverrideGroup { get; set; }

    [UiProperty("Ingombro lungo la linea", "mm", "Geometria", Order = 2)]
    public double FootprintLengthMm { get; set; } = 1200;

    [UiProperty("Lato macchina", "", "Geometria", Order = 3)]
    public LateralSide Side { get; set; } = LateralSide.Left;

    /// <summary>
    /// Carica del filo (KNCA) o riarmo (SMA). KNCA: parte quando la testa del pacco impegna la fotocellula
    /// di riferimento a monte della legatrice e, tra le legature dello stesso pattern, in automatico;
    /// a pattern concluso attende il pacco successivo. SMA: riarmo automatico dopo ogni ciclo (sempre armata).
    /// </summary>
    [UiProperty("Tempo di ricarica filo / riarmo reggia", "s", "Tempi", Order = 9)]
    public double ArmingTimeS { get; set; } = KncaArmingTimeS;

    [UiProperty("Tempo di preparazione aggiuntivo", "s", "Tempi", Order = 10)]
    public double PreparationTimeS { get; set; }

    [UiProperty("Ciclo di legatura (abbassa, lega, rialza)", "s", "Tempi", Order = 11)]
    public double StrappingTimeS { get; set; } = KncaStrappingTimeS;

    [UiProperty("Tempo di ritorno aggiuntivo", "s", "Tempi", Order = 12)]
    public double ReturnTimeS { get; set; }

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
    /// <summary>
    /// Posizione della battuta: calcolata (non si imposta a mano) come posizione dell'ultimo rullo dell'ultima via a rulli
    /// più <see cref="DistanceFromLastRollerMm"/>. Il fermo finale sta sempre dopo l'ultimo rullo.
    /// </summary>
    [UiProperty("Posizione battuta (fine ultimo modulo)", "mm", "Geometria", ReadOnly = true, Order = 1)]
    public double PositionMm { get; set; }

    /// <summary>Distanza tra l'ultimo rullo e il fermo finale: il fermo sta sempre alla fine dell'ultimo modulo, quindi vale la sporgenza (metà passo).</summary>
    [UiProperty("Distanza ultimo rullo - fermo (= sporgenza modulo)", "mm", "Geometria", ReadOnly = true, Order = 0)]
    public double DistanceFromLastRollerMm { get; set; } = 100;

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

    /// <summary>Velocità massima d'impatto sul fermo: 0,3 m/s, uguale alla velocità minima dei moduli (arrivo dopo il rallentamento).</summary>
    [UiProperty("Velocità massima d'impatto ammessa", "m/s", "Sicurezza", Order = 20)]
    public double MaxImpactSpeed { get; set; } = DefaultMaxImpactSpeed;

    public const double DefaultMaxImpactSpeed = 0.3;
}

/// <summary>Trasferitore trasversale a valle dello stopper.</summary>
/// <summary>Sezione del trasferitore sollevabile.</summary>
public sealed class TransferSection
{
    public string Name { get; set; } = "Sezione";
    public double LengthMm { get; set; } = 6000;
}

/// <summary>Tipo di trasferitore: a rulli/forche sollevabili oppure a catene.</summary>
public enum TransferKind { Lifting, Chains }

public sealed class TransferConfig : ComponentBase
{
    /// <summary>Il trasferitore sollevabile è sempre a catene (si disegna così): il tipo non si sceglie più dall'interfaccia.</summary>
    public TransferKind Kind { get; set; } = TransferKind.Lifting;

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

    /// <summary>Larghezza del trasferitore a catene: lunghezza delle catene trasversali alla linea.</summary>
    [UiProperty("Larghezza trasferitore", "mm", "Geometria", Order = 3)]
    public double WidthMm { get; set; } = 3000;

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

    /// <summary>
    /// Non più usato: la fotocellula di arresto davanti al trasferitore segue la regola comune (almeno 2 m o lo spazio di
    /// frenata, vedi PhotocellRules). Mantenuto per leggere i progetti salvati.
    /// </summary>
    public double StopPhotocellMarginMm { get; set; } = 1000;

    /// <summary>
    /// Non più usato dal motore: i pacchi per ciclo dipendono dal banking scelto nella sezione Simulazioni
    /// (single = 1, double = 2). Mantenuto per leggere i progetti salvati.
    /// </summary>
    public int BatchSize { get; set; } = 1;

    /// <summary>Double banking senza fermo scomparente: distanza tra coda del primo pacco e testa del secondo, fermato per posizionamento (encoder).</summary>
    [UiProperty("Distanza tra i pacchi in double banking (senza fermo)", "mm", "Funzionamento", Order = 24)]
    public double DoubleBankingGapMm { get; set; } = DefaultDoubleBankingGapMm;

    /// <summary>Sbalzo massimo ammesso del pacco a monte oltre l'inizio del trasferitore: mai oltre 2 m.</summary>
    [UiProperty("Sbalzo ammesso del pacco oltre il trasferitore (max 2000)", "mm", "Funzionamento", Order = 25)]
    public double MaxOverhangMm { get; set; } = 500;

    /// <summary>Valori del double banking senza fermo scomparente (decisi con l'utente il 2026-11-08).</summary>
    public const double DefaultDoubleBankingGapMm = 500, OverhangLimitMm = 2000;

    /// <summary>Sbalzo usato dal motore e dai controlli: quello impostato, mai oltre 2 m.</summary>
    [JsonIgnore]
    public double EffectiveMaxOverhangMm => Math.Clamp(MaxOverhangMm, 0, OverhangLimitMm);

    /// <summary>
    /// Bilico a metà corsa (trasferitore sollevabile a due prese): a ogni ciclo il trasferitore avanza di mezza corsa e porta
    /// insieme il pacco nuovo dalla via rulli al bilico e quello già pesato dal bilico al trasferitore d'accumulo.
    /// Al primo ciclo c'è solo il pacco della via rulli.
    /// </summary>
    [UiProperty("Bilico a metà corsa (due prese: via rulli → bilico → accumulo)", "", "Funzionamento", Order = 26)]
    public bool ScaleAtMidStroke { get; set; }

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

/// <summary>Famiglia delle legatrici: tempi e ingombro comuni ereditati dalle legatrici non personalizzate.</summary>
public sealed class StrapperGroup : ComponentBase
{
    public StrapperGroup() { Id = "LEG"; Name = "Legatrici"; }

    [UiProperty("Ingombro lungo la linea (multipli di 100 mm)", "mm", "Geometria", Order = 2)]
    public double FootprintLengthMm { get; set; } = 1200;

    [UiProperty("Tempo di ricarica filo / riarmo reggia", "s", "Tempi", Order = 9)]
    public double ArmingTimeS { get; set; } = StrapperConfig.KncaArmingTimeS;

    [UiProperty("Tempo di preparazione aggiuntivo", "s", "Tempi", Order = 10)]
    public double PreparationTimeS { get; set; }

    [UiProperty("Ciclo di legatura (abbassa, lega, rialza)", "s", "Tempi", Order = 11)]
    public double StrappingTimeS { get; set; } = StrapperConfig.KncaStrappingTimeS;

    [UiProperty("Tempo di ritorno aggiuntivo", "s", "Tempi", Order = 12)]
    public double ReturnTimeS { get; set; }

    [UiProperty("Può lavorare in combinazione", "", "Funzionamento", Order = 21)]
    public bool AllowCombinedWork { get; set; } = true;

    [UiProperty("Allarme se attesa consenso stringifascio oltre", "s", "Interblocchi", Order = 30)]
    public double ConsentTimeoutS { get; set; } = 30;

    [UiProperty("Tempo di ripristino dopo guasto", "s", "Diagnostica", Order = 41)]
    public double FaultResetTimeS { get; set; } = 30;
}

/// <summary>
/// Trasferitore d'accumulo in uscita dal trasferitore sollevabile: riceve i pacchi pesati e, a ogni deposito, avanza di un
/// passo pari alla larghezza del pacco + <see cref="StepGapMm"/>, liberando il punto di deposito. I pacchi che arrivano in
/// fondo (larghezza utile <see cref="WidthMm"/>) vengono scaricati.
/// </summary>
public sealed class AccumulationTransferConfig : ComponentBase
{
    public AccumulationTransferConfig() { Id = "TRA"; Name = "Trasferitore d'accumulo"; }

    /// <summary>Sempre presente (regola dell'utente 2026-11-10): non si toglie più.</summary>
    public bool IsEnabled { get; set; } = true;

    [UiProperty("Larghezza utile (verso di accumulo)", "mm", "Geometria", Order = 10)]
    public double WidthMm { get; set; } = 5000;

    [UiProperty("Distanza tra i pacchi (passo = larghezza pacco + distanza)", "mm", "Geometria", Order = 11)]
    public double StepGapMm { get; set; } = 50;

    [UiProperty("Velocità di avanzamento", "m/s", "Tempi", Order = 20)]
    public double StepSpeed { get; set; } = 0.2;
}
