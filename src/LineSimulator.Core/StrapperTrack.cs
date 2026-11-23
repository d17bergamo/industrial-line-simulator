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
        // le legatrici non escono dalla via di legatura (la prima): se l'ultima è costretta a valle, la fine della via la segue
        var sec = p.Sections.OrderBy(x => x.StartMm).FirstOrDefault();
        if (sec == null) return 0;
        double deficit = MinRollersAfterLastStrapper * sec.RollerPitchMm - (sec.EndMm - last.PositionMm);
        // tolleranza: allungando la via il passo viene ricalcolato di poco (le giunzioni hanno la precedenza)
        if (deficit <= Math.Max(0.5, 0.1 * sec.RollerPitchMm)) return 0;
        double oldEnd = sec.EndMm;
        var saved = p.Strappers.Select(x => (x, x.PositionMm)).ToList();     // le legatrici non seguono lo spostamento a valle: è la via che raggiunge loro
        LineReflow.Fit(sec, sec.StartMm, oldEnd + deficit);
        LineReflow.AfterResize(p, sec, oldEnd);
        foreach (var (x, pos) in saved) x.PositionMm = pos;
        return deficit;
    }

    /// <summary>Distanza minima di una legatrice dall'inizio della via rulli di legatura.</summary>
    public const double MinStrapperFromTrackStartMm = 1000;

    /// <summary>Inizio della via rulli di legatura (prima via), null se non ci sono vie.</summary>
    public static double? TrackStartMm(LineProject p) => p.Sections.Count == 0 ? null : p.Sections.Min(s => s.StartMm);

    /// <summary>Distanza minima della prima legatrice da FC0 (decisa con l'utente il 2026-11-13).</summary>
    public const double MinStrapperFromEntryMm = 1500;

    /// <summary>
    /// Posizione minima ammessa per una legatrice: 1 m dall'inizio della via, almeno 1500 mm da FC0 e comunque lo spazio
    /// per la regola dell'encoder (fotocellula di riferimento ≥ FC0 + 100 mm, encoder 300 mm dopo, encoder ≥ 1 m prima della legatrice).
    /// </summary>
    public static double MinStrapperPositionMm(LineProject p)
    {
        double fc0 = StrappingAreaRules.MinReferencePhotocellMm(p) - StrappingAreaRules.MinReferenceAfterEntryMm;
        return new[]
        {
            (TrackStartMm(p) ?? 0) + MinStrapperFromTrackStartMm,
            fc0 + MinStrapperFromEntryMm,
            StrappingAreaRules.MinReferencePhotocellMm(p) + EncoderRule.ReferenceOffsetMm + EncoderRule.MinToStrapperMm
        }.Max();
    }

    /// <summary>Riporta a 1 m dall'inizio della via le legatrici più vicine. Restituisce true se ne ha spostata qualcuna.</summary>
    public static bool ClampStrappersToTrackStart(LineProject p)
    {
        double min = MinStrapperPositionMm(p);
        bool changed = false;
        foreach (var s in p.Strappers.Where(x => x.PositionMm < min - 1e-6)) { s.PositionMm = min; changed = true; }
        return changed;
    }

    /// <summary>
    /// Encoder e fotocellula di riferimento di legatura restano ad almeno 1 m a monte della prima legatrice: se la legatrice
    /// si avvicina di più, si spostano con lei (la fotocellula sta 300 mm prima dell'encoder). True se qualcosa è stato spostato.
    /// </summary>
    public static bool KeepEncoderBehindFirstStrapper(LineProject p) => EncoderRule.Apply(p).Count > 0;   // regola unica dell'encoder
}
