using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Identificativo dei pacchi: sempre "P" (pacco) + lunghezza in metri + tipo di legatura tra parentesi,
/// per esempio "P12 (Wire)" oppure "P18 (Still Strapp)". L'identificativo segue lunghezza e tipo di legatura.
/// </summary>
public static class PackageNaming
{
    /// <summary>Distanza di default delle legature di testa e di coda dalle estremità del pacco.</summary>
    public const double DefaultEndDistanceMm = 500;

    public static string BindingLabel(BindingType b) => b == BindingType.SteelStrap ? "Still Strapp" : "Wire";

    public static string IdFor(double lengthMm, BindingType b)
        => $"P{(lengthMm / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} ({BindingLabel(b)})";

    public static string IdFor(PackageType t) => IdFor(t.LengthMm, t.BindingType);

    /// <summary>Id univoco nel progetto: se già usato da un altro tipo aggiunge un indice (" #2").</summary>
    public static string UniqueId(LineProject p, PackageType t)
    {
        string baseId = IdFor(t), id = baseId;
        for (int i = 2; p.PackageTypes.Any(o => !ReferenceEquals(o, t) && o.Id == id); i++) id = $"{baseId} #{i}";
        return id;
    }

    /// <summary>Riallinea l'id del tipo a lunghezza e legatura e aggiorna i riferimenti. Restituisce true se l'id è cambiato.</summary>
    public static bool Refresh(LineProject p, PackageType t)
    {
        string old = t.Id, id = UniqueId(p, t);
        if (old == id) return false;
        t.Id = id;
        if (string.IsNullOrEmpty(old)) return true;
        p.Infeed.TypeSequence = p.Infeed.TypeSequence.Select(x => x == old ? id : x).ToList();
        p.Rejector.RejectTypeIds = p.Rejector.RejectTypeIds.Select(x => x == old ? id : x).ToList();
        foreach (var plan in p.StrappingStation.ManualPlans.Where(m => m.PackageTypeId == old)) plan.PackageTypeId = id;
        return true;
    }

    /// <summary>Porta all'id standard tutti i tipi con id nel vecchio formato "P12" (progetti salvati prima della regola).</summary>
    public static bool MigrateLegacyIds(LineProject p)
    {
        bool changed = false;
        foreach (var t in p.PackageTypes.ToList())
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(t.Id ?? "", @"^P\d+(\.\d+)?$")) continue;
            changed |= Refresh(p, t);
        }
        return changed;
    }

    /// <summary>Legature di default: testa e coda a 500 mm dalle estremità, le altre equidistanti (a passi di 100 mm), simmetriche.</summary>
    public static List<double> DefaultStraps(double lengthMm, int count)
    {
        double e = DefaultEndDistanceMm;
        count = Math.Max(1, count);
        if (lengthMm <= 2 * e || count == 1) return new List<double> { Math.Round(lengthMm / 2 / 100) * 100 };
        var pos = new List<double>();
        for (int i = 0; i < count; i++)
        {
            double r = Math.Round((e + i * (lengthMm - 2 * e) / (count - 1)) / 100.0) * 100;
            if (i >= (count + 1) / 2) r = lengthMm - pos[count - 1 - i];
            pos.Add(r);
        }
        pos[0] = e; pos[count - 1] = lengthMm - e;
        return pos;
    }
}
