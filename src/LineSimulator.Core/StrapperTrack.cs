using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Via a rulli a valle dell'ultima legatrice: almeno 2 m (anche con una sola legatrice) e, se si sposta l'ultima legatrice,
/// con lei si sposta tutta la parte a valle (fine della sua via, vie successive, fotocellule, trasferitore e bilici).
/// </summary>
public static class StrapperTrack
{
    /// <summary>Lunghezza minima di via a rulli tra l'ultima legatrice e la fine della sua via.</summary>
    public const double MinTrackAfterLastStrapperMm = 2000;

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
        double deficit = MinTrackAfterLastStrapperMm - (sec.EndMm - last.PositionMm);
        if (deficit <= 0.5) return 0;
        double oldEnd = sec.EndMm;
        LineReflow.Fit(sec, sec.StartMm, oldEnd + deficit);
        LineReflow.AfterResize(p, sec, oldEnd);
        return deficit;
    }

    /// <summary>
    /// L'ultima legatrice è stata spostata da oldPositionMm alla posizione attuale: la parte a valle la segue (la via che la contiene
    /// si allunga o accorcia e tutto ciò che sta oltre si sposta). Per le altre legatrici non cambia nulla.
    /// Restituisce true se ha spostato la parte a valle.
    /// </summary>
    public static bool FollowLastStrapper(LineProject p, StrapperConfig moved, double oldPositionMm)
    {
        double dx = moved.PositionMm - oldPositionMm;
        if (Math.Abs(dx) < 1e-9) return false;
        bool wasLast = p.Strappers.Where(s => s.IsEnabled && !ReferenceEquals(s, moved)).All(s => s.PositionMm <= oldPositionMm + 1e-6);
        if (!wasLast) return false;
        var sec = SectionAt(p, oldPositionMm);
        if (sec == null) return false;
        double oldEnd = sec.EndMm;
        double minEnd = sec.StartMm + LineReflow.MinLengthMm(sec);
        if (oldEnd + dx < minEnd) dx = minEnd - oldEnd;
        moved.PositionMm = oldPositionMm;           // la legatrice resta ferma mentre si sposta la parte a valle
        LineReflow.ShiftBeyond(p, oldPositionMm, dx);
        LineReflow.Fit(sec, sec.StartMm, oldEnd + dx);
        moved.PositionMm = oldPositionMm + dx;
        return true;
    }
}
