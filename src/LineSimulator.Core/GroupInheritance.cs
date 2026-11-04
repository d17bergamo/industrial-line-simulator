using System.Reflection;
using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Famiglie padre/figli generiche (fotocellule, legatrici, bilici): ogni proprietà del padre con lo stesso nome del figlio
/// è ereditata dai figli non personalizzati; solo con la spunta "personalizza" il singolo si sblocca.
/// </summary>
public static class GroupInheritance
{
    private static readonly string[] Skip = { "Id", "Name" };

    /// <summary>Nomi delle proprietà ereditate dal padre.</summary>
    public static IReadOnlySet<string> InheritedNames(object group) =>
        group.GetType().GetProperties()
            .Where(p => p.CanWrite && p.GetCustomAttribute<UiPropertyAttribute>() != null && !Skip.Contains(p.Name))
            .Select(p => p.Name).ToHashSet();

    private static IEnumerable<(PropertyInfo G, PropertyInfo C)> Pairs(object group, object child)
    {
        foreach (var g in group.GetType().GetProperties())
        {
            if (!g.CanWrite || g.GetCustomAttribute<UiPropertyAttribute>() == null || Skip.Contains(g.Name)) continue;
            var c = child.GetType().GetProperty(g.Name);
            if (c != null && c.CanWrite && c.PropertyType == g.PropertyType) yield return (g, c);
        }
    }

    /// <summary>Copia i valori del padre su tutti i figli non personalizzati. True se qualcosa è cambiato.</summary>
    public static bool Propagate(object group, IEnumerable<IGroupChild> children)
    {
        bool changed = false;
        foreach (var kid in children.Where(k => !k.OverrideGroup))
            foreach (var (g, c) in Pairs(group, kid))
            {
                var v = g.GetValue(group);
                if (!Equals(c.GetValue(kid), v)) { c.SetValue(kid, v); changed = true; }
            }
        return changed;
    }

    /// <summary>Progetti precedenti: i figli con valori diversi dal padre di default restano come sono e vengono marcati "personalizzati".</summary>
    public static bool MarkCustomized(object group, IEnumerable<IGroupChild> children)
    {
        bool changed = false;
        foreach (var kid in children.Where(k => !k.OverrideGroup))
            if (Pairs(group, kid).Any(pr => !Equals(pr.C.GetValue(kid), pr.G.GetValue(group)))) { kid.OverrideGroup = true; changed = true; }
        return changed;
    }

    public static bool PropagateAll(LineProject p)
    {
        bool a = Propagate(p.PhotocellDefaults, p.Photocells);
        bool b = Propagate(p.StrapperDefaults, p.Strappers);
        bool c = Propagate(p.ScaleDefaults, p.Scales);
        return a | b | c;
    }

    public static bool MarkAllCustomized(LineProject p)
    {
        bool a = MarkCustomized(p.PhotocellDefaults, p.Photocells);
        bool b = MarkCustomized(p.StrapperDefaults, p.Strappers);
        bool c = MarkCustomized(p.ScaleDefaults, p.Scales);
        return a | b | c;
    }
}
