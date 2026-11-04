using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Via a rulli attorno alle legatrici: almeno 1 m dall'inizio della via a monte e almeno due rulli a valle dell'ultima
/// legatrice (anche con una sola). La via si allunga solo se l'ultima legatrice arriva a meno di due rulli dalla sua fine.
/// </summary>
public static class StrapperTrack
{
    /// <summary>Rulli minimi di via di legatura dopo l'ultima legatrice (regola: più corta di così non può essere).</summary>
    public const int MinRollersAfterLastStrapper = 2;

    /// <summary>L'ultima legatrice abilitata lungo la linea, null se non ce ne sono.</summary>
    public static StrapperConfig? LastStrapper(LineProject p) =>
        p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).LastOrDefault();

    /// <summary>Via a rulli che contiene la posizione indicata (inizio incluso, fine esclusa), null se fuori dalle vie.</summary>
    public static RollerSection? SectionAt(LineProject p, double posMm) =>
        p.Sections.OrderBy(s => s.StartMm).FirstOrDefault(s => posMm >= s.StartMm - 1e-6 && posMm < s.EndMm - 1e-6);

    /// <summary>
    /// Se tra l'ultima legatrice e la fine della sua via ci sono meno di 2 m, allunga la via (le vie e gli equipaggiamenti a valle
    /// avanzano). Restituisce la lunghezza aggiunta in mm (0 = niente da fare).
    /// </summary>
    public static double EnsureMinimumTrack(LineProject p)
    {
        var last = LastStrapper(p);
        if (last == null) return 0;
        var sec = SectionAt(p, last.PositionMm);
        if (sec == null) return 0;
        double deficit = MinRollersAfterLastStrapper * sec.RollerPitchMm - (sec.EndMm - last.PositionMm);
        // tolleranza: allungando la via il passo viene ricalcolato di poco (le giunzioni hanno la precedenza)
        if (deficit <= Math.Max(0.5, 0.1 * sec.RollerPitchMm)) return 0;
        double oldEnd = sec.EndMm;
        LineReflow.Fit(sec, sec.StartMm, oldEnd + deficit);
        LineReflow.AfterResize(p, sec, oldEnd);
        return deficit;
    }

    /// <summary>Distanza minima di una legatrice dall'inizio della via rulli di legatura.</summary>
    public const double MinStrapperFromTrackStartMm = 1000;

    /// <summary>Inizio della via rulli di legatura (prima via), null se non ci sono vie.</summary>
    public static double? TrackStartMm(LineProject p) => p.Sections.Count == 0 ? null : p.Sections.Min(s => s.StartMm);

    /// <summary>Posizione minima ammessa per una legatrice (1 m dall'inizio della via), 0 se non ci sono vie.</summary>
    public static double MinStrapperPositionMm(LineProject p) => (TrackStartMm(p) ?? 0) + MinStrapperFromTrackStartMm;

    /// <summary>Riporta a 1 m dall'inizio della via le legatrici più vicine. Restituisce true se ne ha spostata qualcuna.</summary>
    public static bool ClampStrappersToTrackStart(LineProject p)
    {
        double min = MinStrapperPositionMm(p);
        bool changed = false;
        foreach (var s in p.Strappers.Where(x => x.PositionMm < min - 1e-6)) { s.PositionMm = min; changed = true; }
        return changed;
    }

    /// <summary>Distanza minima tra l'encoder / fotocellula di legatura e la prima legatrice (a monte di essa).</summary>
    public const double MinEncoderToStrapperMm = 1000;

    /// <summary>
    /// Encoder e fotocellula di riferimento di legatura restano ad almeno 1 m a monte della prima legatrice: se la legatrice
    /// si avvicina di più, si spostano con lei (la fotocellula sta 300 mm prima dell'encoder). True se qualcosa è stato spostato.
    /// </summary>
    public static bool KeepEncoderBehindFirstStrapper(LineProject p)
    {
        var first = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).FirstOrDefault();
        if (first == null || !p.StrappingEncoder.IsEnabled) return false;
        double start = TrackStartMm(p) ?? 0;
        double enc = p.StrappingEncoder.PositionMm;
        if (first.PositionMm - enc >= MinEncoderToStrapperMm - 1e-6) return false;
        enc = Math.Max(start, first.PositionMm - MinEncoderToStrapperMm);
        p.StrappingEncoder.PositionMm = enc;
        foreach (var pc in p.Photocells.Where(x => x.Purpose == PhotocellPurpose.StrappingReference))
            pc.PositionMm = Math.Max(start + 100, enc - ProjectNormalizer.ReferencePhotocellOffsetMm);   // sempre dopo la prima fotocellula
        return true;
    }
}
