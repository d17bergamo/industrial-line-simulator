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

/// <summary>
/// Proprietà (anche ereditate) da non mostrare nel pannello proprietà per questa classe.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UiHiddenPropertiesAttribute : Attribute
{
    public UiHiddenPropertiesAttribute(params string[] names) => Names = names;
    public IReadOnlyList<string> Names { get; }
}

/// <summary>Posizione di un dispositivo sul trasferitore sollevabile, scelta all'inserimento.</summary>
public enum DevicePlacement
{
    /// <summary>Al centro del trasferitore.</summary>
    Center = 0,
    /// <summary>All'estremità del trasferitore (il fermo scomparente all'estremo a monte, opposto al fermo fisso).</summary>
    End = 1
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
