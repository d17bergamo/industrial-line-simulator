namespace LineSimulator.Models;

/// <summary>
/// Metadati per il pannello proprietà generico dell'interfaccia.
/// Il modello non dipende dalla UI: l'attributo è solo descrittivo.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UiPropertyAttribute : Attribute
{
    public UiPropertyAttribute(string displayName, string unit = "", string category = "Generale")
    {
        DisplayName = displayName;
        Unit = unit;
        Category = category;
    }

    public string DisplayName { get; }
    public string Unit { get; }
    public string Category { get; }
    public bool ReadOnly { get; set; }
    public int Order { get; set; }
}

/// <summary>Base comune di tutti i componenti della linea.</summary>
public abstract class ComponentBase
{
    [UiProperty("Identificativo", Order = -100)]
    public string Id { get; set; } = string.Empty;

    [UiProperty("Nome", Order = -99)]
    public string Name { get; set; } = string.Empty;

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";
}

public enum TransportDirection
{
    /// <summary>Movimento nel verso positivo dell'asse linea (unico supportato dal motore v1).</summary>
    Forward = 0,
    /// <summary>Movimento inverso: memorizzato ma segnalato come non supportato dal validatore.</summary>
    Reverse = 1
}

public enum SpeedLevel
{
    Low = 0,
    High = 1
}

public enum LateralSide
{
    Left = 0,
    Right = 1
}

/// <summary>Componente figlio di una famiglia: segue le proprietà del padre finché non è spuntato "personalizza".</summary>
public interface IGroupChild
{
    bool OverrideGroup { get; set; }
}
