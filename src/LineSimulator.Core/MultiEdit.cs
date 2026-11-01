using System.Collections;
using System.Reflection;
using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Proprietà modificabile (o in sola lettura) esposta nel pannello proprietà.</summary>
public sealed record EditableProperty(PropertyInfo Property, UiPropertyAttribute Meta)
{
    public string Name => Property.Name;
    public bool IsReadOnly => Meta.ReadOnly || !Property.CanWrite;
}

/// <summary>
/// Modifica contemporanea di più componenti dello stesso tipo. Solo il parametro effettivamente modificato
/// viene scritto sui componenti selezionati: gli altri parametri restano ai valori propri di ciascuno.
/// </summary>
public static class MultiEdit
{
    public static IReadOnlyList<EditableProperty> Describe(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p, a: p.GetCustomAttribute<UiPropertyAttribute>()))
            .Where(x => x.a != null)
            .OrderBy(x => x.a!.Order)
            .Select(x => new EditableProperty(x.p, x.a!))
            .ToList();

    /// <summary>True se tutti gli oggetti hanno lo stesso tipo (condizione per la modifica contemporanea).</summary>
    public static bool SameType(IReadOnlyList<object> targets, out Type? type)
    {
        var first = targets.Count > 0 ? targets[0].GetType() : null;
        type = first;
        return first != null && targets.All(t => t.GetType() == first);
    }

    /// <summary>Valore comune della proprietà, oppure Mixed = true se i componenti differiscono.</summary>
    public static (bool Mixed, object? Value) Read(IReadOnlyList<object> targets, PropertyInfo prop)
    {
        if (targets.Count == 0) return (false, null);
        var first = prop.GetValue(targets[0]);
        for (int i = 1; i < targets.Count; i++)
            if (!ValueEquals(first, prop.GetValue(targets[i]))) return (true, first);
        return (false, first);
    }

    /// <summary>
    /// Scrive il valore sui componenti in cui è diverso. Restituisce quanti sono stati effettivamente modificati.
    /// Le proprietà in sola lettura non vengono mai scritte.
    /// </summary>
    public static int Apply(IReadOnlyList<object> targets, PropertyInfo prop, object? value)
    {
        if (!prop.CanWrite || prop.GetCustomAttribute<UiPropertyAttribute>()?.ReadOnly == true) return 0;
        int changed = 0;
        foreach (var t in targets)
        {
            if (ValueEquals(prop.GetValue(t), value)) continue;
            prop.SetValue(t, CloneIfList(value));
            changed++;
        }
        return changed;
    }

    private static object? CloneIfList(object? v) => v switch
    {
        List<double> d => new List<double>(d),
        List<string> s => new List<string>(s),
        _ => v
    };

    public static bool ValueEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a is string || b is string) return Equals(a, b);
        if (a is IEnumerable ea && b is IEnumerable eb)
            return ea.Cast<object?>().SequenceEqual(eb.Cast<object?>());
        return Equals(a, b);
    }
}
