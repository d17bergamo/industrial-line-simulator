using System.Text.Json.Serialization;

namespace LineSimulator.Models;

public enum RuleAction
{
    /// <summary>Arresta l'inverter (priorità massima, interblocco).</summary>
    StopInverter = 0,
    /// <summary>Forza la velocità bassa.</summary>
    SetLowSpeed = 1,
    /// <summary>Forza la velocità alta (vince solo se nessuna regola chiede bassa o stop).</summary>
    SetHighSpeed = 2,
    /// <summary>Imposta un segnale logico (consenso) leggibile da altre regole.</summary>
    SetSignal = 3
}

/// <summary>
/// Regola logica: SE condizione ALLORA azione. Valutata a ogni scansione del motore,
/// con gli ingressi della scansione precedente (come un PLC), quindi deterministica.
/// </summary>
public sealed class LogicRule : ComponentBase
{
    [UiProperty("Regola attiva", "", "Regola", Order = 1)]
    public bool IsEnabled { get; set; } = true;

    [UiProperty("Modulo destinazione", "", "Regola", Order = 2)]
    public string TargetInverterId { get; set; } = string.Empty;

    [UiProperty("Azione", "", "Regola", Order = 3)]
    public RuleAction Action { get; set; } = RuleAction.SetLowSpeed;

    [UiProperty("Nome segnale (per SetSignal)", "", "Regola", Order = 4)]
    public string SignalName { get; set; } = string.Empty;

    public LogicCondition Condition { get; set; } = new ConstantCondition { Value = false };

    /// <summary>Condizione in forma testuale modificabile (vedi <see cref="Models.ConditionText"/>).</summary>
    [JsonIgnore]
    [UiProperty("Condizione (es. PC(FC2) & !RUN(INV3))", "", "Regola", Order = 5)]
    public string ConditionExpression
    {
        get => Models.ConditionText.Format(Condition);
        set => Condition = Models.ConditionText.Parse(value);
    }

    [JsonIgnore]
    [UiProperty("Condizione in chiaro", "", "Regola", ReadOnly = true, Order = 6)]
    public string ConditionDescription => Condition?.Describe() ?? "(nessuna)";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AndCondition), "and")]
[JsonDerivedType(typeof(OrCondition), "or")]
[JsonDerivedType(typeof(NotCondition), "not")]
[JsonDerivedType(typeof(ConstantCondition), "const")]
[JsonDerivedType(typeof(PhotocellCondition), "photocell")]
[JsonDerivedType(typeof(InverterRunningCondition), "inverterRunning")]
[JsonDerivedType(typeof(ZoneOccupiedCondition), "zoneOccupied")]
[JsonDerivedType(typeof(StrapperBusyCondition), "strapperBusy")]
[JsonDerivedType(typeof(StopperOccupiedCondition), "stopperOccupied")]
[JsonDerivedType(typeof(TransferAtHomeCondition), "transferHome")]
[JsonDerivedType(typeof(PackageHeadInRangeCondition), "headInRange")]
[JsonDerivedType(typeof(GapAheadLessThanCondition), "gapAhead")]
[JsonDerivedType(typeof(SignalCondition), "signal")]
[JsonDerivedType(typeof(DeviceStateCondition), "device")]
public abstract class LogicCondition
{
    public abstract string Describe();
}

public sealed class AndCondition : LogicCondition
{
    public List<LogicCondition> Children { get; set; } = new();
    public override string Describe() => "(" + string.Join(" AND ", Children.Select(c => c.Describe())) + ")";
}

public sealed class OrCondition : LogicCondition
{
    public List<LogicCondition> Children { get; set; } = new();
    public override string Describe() => "(" + string.Join(" OR ", Children.Select(c => c.Describe())) + ")";
}

public sealed class NotCondition : LogicCondition
{
    public LogicCondition Child { get; set; } = new ConstantCondition();
    public override string Describe() => "NOT " + Child.Describe();
}

public sealed class ConstantCondition : LogicCondition
{
    public bool Value { get; set; }
    public override string Describe() => Value ? "VERO" : "FALSO";
}

public sealed class PhotocellCondition : LogicCondition
{
    public string PhotocellId { get; set; } = string.Empty;
    public bool Occupied { get; set; } = true;
    public override string Describe() => $"FC[{PhotocellId}] {(Occupied ? "occupata" : "libera")}";
}

public sealed class InverterRunningCondition : LogicCondition
{
    public string InverterId { get; set; } = string.Empty;
    public bool Running { get; set; } = true;
    public override string Describe() => $"MOD[{InverterId}] {(Running ? "in moto" : "fermo")}";
}

public sealed class ZoneOccupiedCondition : LogicCondition
{
    public string InverterId { get; set; } = string.Empty;
    public override string Describe() => $"Modulo[{InverterId}] occupato";
}

public sealed class StrapperBusyCondition : LogicCondition
{
    public string StrapperId { get; set; } = string.Empty;
    public override string Describe() => $"LEG[{StrapperId}] occupata";
}

public sealed class StopperOccupiedCondition : LogicCondition
{
    public override string Describe() => "Pacco allo stopper";
}

public sealed class TransferAtHomeCondition : LogicCondition
{
    public override string Describe() => "Trasferitore in posizione";
}

public sealed class PackageHeadInRangeCondition : LogicCondition
{
    public double FromMm { get; set; }
    public double ToMm { get; set; }
    public override string Describe() => $"Testa pacco in [{FromMm:0};{ToMm:0}] mm";
}

public sealed class GapAheadLessThanCondition : LogicCondition
{
    /// <summary>Considera i pacchi la cui testa è nella zona di questo inverter.</summary>
    public string InverterId { get; set; } = string.Empty;
    public double GapMm { get; set; } = 1000;
    public override string Describe() => $"Distanza da pacco precedente nel modulo[{InverterId}] < {GapMm:0} mm";
}

public sealed class SignalCondition : LogicCondition
{
    public string SignalName { get; set; } = string.Empty;
    public override string Describe() => $"Segnale[{SignalName}]";
}

/// <summary>
/// Stato di un dispositivo (legatrice, stringifascio, compattatore, rigettatrice, bilico, fermo, trasferitore).
/// Senza stato: vero se il dispositivo è impegnato (non a riposo).
/// </summary>
public sealed class DeviceStateCondition : LogicCondition
{
    public string DeviceId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public override string Describe() => string.IsNullOrWhiteSpace(State) ? $"DISP[{DeviceId}] impegnato" : $"DISP[{DeviceId}] in stato {State}";
}
