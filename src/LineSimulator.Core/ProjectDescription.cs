using System.Text.RegularExpressions;
using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Nome e descrizione del progetto che si aggiornano con le legatrici e i pacchi presenti.</summary>
public static class ProjectDescription
{
    private static string Machines(LineProject p)
    {
        int n = p.Strappers.Count(s => s.IsEnabled);
        if (n == 0) return "nessuna legatrice";
        bool wire = p.Strappers.Any(s => s.IsEnabled && s.MachineType == StrapperMachineType.KNCA);
        bool strap = p.Strappers.Any(s => s.IsEnabled && s.MachineType == StrapperMachineType.SMA);
        string noun = strap && !wire ? (n == 1 ? "reggettatrice" : "reggettatrici") : (n == 1 ? "legatrice" : "legatrici");
        return $"{n} {noun}";
    }

    private static string Packages(LineProject p) =>
        p.PackageTypes.Count == 0 ? "nessun pacco"
            : "pacchi " + string.Join("/", p.PackageTypes.Select(t => (t.LengthMm / 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Distinct()) + " m";

    /// <summary>Nome automatico, es. "2 legatrici - pacchi 6/9/12 m".</summary>
    public static string AutoName(LineProject p) => $"{Machines(p)} - {Packages(p)}";

    /// <summary>True se il nome è uno generato automaticamente (o dell'avvio guidato) e può essere aggiornato.</summary>
    public static bool IsAutoName(string name) =>
        string.IsNullOrWhiteSpace(name) || name == "Nuovo layout" || Regex.IsMatch(name, @"^(\d+ (legatric|reggettatric)\w*|nessuna legatrice) - (pacchi|nessun pacco)");

    public static string Build(LineProject p)
    {
        int scales = p.Scales.Count(s => s.IsEnabled);
        var parts = new List<string>
        {
            Machines(p),
            Packages(p),
            $"{p.Sections.Count} {(p.Sections.Count == 1 ? "modulo" : "moduli")} via rulli, {p.Sections.Sum(s => s.LengthMm) / 1000:0.#} m",
            "trasferitore sollevabile a catene + trasferitore d'accumulo"
        };
        if (scales > 0) parts.Add($"{scales} {(scales == 1 ? "bilico" : "bilici")} {(p.Scales.Any(s => s.Mounting == ScaleMounting.InLine) ? "integrati nella via a rulli" : "bancale")}");
        if (p.IntermediateStops.Any(f => f.IsEnabled)) parts.Add("fermo scomparente");
        return string.Join(" · ", parts);
    }

    /// <summary>Aggiorna descrizione (sempre) e nome (se automatico). True se qualcosa è cambiato.</summary>
    public static bool Refresh(LineProject p)
    {
        bool changed = false;
        string d = Build(p);
        if (p.Description != d) { p.Description = d; changed = true; }
        if (IsAutoName(p.Name)) { string n = AutoName(p); if (p.Name != n) { p.Name = n; changed = true; } }
        return changed;
    }
}
