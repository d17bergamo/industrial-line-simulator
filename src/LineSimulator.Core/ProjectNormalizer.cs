using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Adeguamenti automatici dei progetti (migrazioni e requisiti obbligatori).</summary>
public static class ProjectNormalizer
{
    /// <summary>Distanza della fotocellula di ingresso aggiunta automaticamente dall'inizio linea.</summary>
    public const double DefaultEntryOffsetMm = 300;

    /// <summary>
    /// Garantisce la presenza della fotocellula di ingresso (obbligatoria). Se nessuna fotocellula è marcata
    /// come ingresso ne aggiunge una (solo segnale) a 300 mm dall'inizio linea. Restituisce true se ha modificato il progetto.
    /// </summary>
    public static bool EnsureEntryPhotocell(LineProject p)
    {
        if (p.Photocells.Any(pc => pc.IsEntryPhotocell)) return false;
        double start = p.Sections.Count > 0 ? p.Sections.Min(s => s.StartMm) : 0;
        var ids = p.Photocells.Select(x => x.Id).ToHashSet();
        string id = "FC0";
        for (int n = 0; ids.Contains(id); n++) id = "FCI" + n;
        p.Photocells.Insert(0, new PhotocellConfig
        {
            Id = id,
            Name = "Fotocellula di ingresso",
            PositionMm = start + DefaultEntryOffsetMm,
            ResponseTimeMs = 20,
            IsEntryPhotocell = true,
            Action = PhotocellAction.None
        });
        return true;
    }
}
