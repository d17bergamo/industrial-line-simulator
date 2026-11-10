using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Padre/figli dei moduli via rulli: i parametri del padre (<see cref="LineProject.SectionDefaults"/>) valgono per tutti i moduli
/// che non hanno la spunta "personalizza". Solo con la spunta il singolo modulo si sblocca e si modifica da solo.
/// </summary>
public static class RollerGroups
{
    /// <summary>Proprietà del modulo che sono governate dal padre (bloccate finché il modulo non è personalizzato).</summary>
    public static readonly IReadOnlySet<string> InheritedProperties = new HashSet<string>
    {
        nameof(RollerSection.WidthMm), nameof(RollerSection.RollerPitchMm),
        nameof(RollerSection.LowSpeed), nameof(RollerSection.HighSpeed), nameof(RollerSection.MaxSpeed),
        nameof(RollerSection.Acceleration), nameof(RollerSection.Deceleration), nameof(RollerSection.DefaultSpeed),
        nameof(RollerSection.AutoStopWhenEmpty)
    };

    /// <summary>Copia sul modulo i valori del padre (senza il passo, che richiede il ricalcolo dei rulli).</summary>
    public static void CopyDrive(RollerGroup g, RollerSection s)
    {
        s.WidthMm = g.WidthMm;           // il diametro dei rulli si imposta per modulo (dal padre vale per tutti, vedi SetDiameterForAll)
        s.LowSpeed = g.LowSpeed; s.HighSpeed = g.HighSpeed; s.MaxSpeed = g.MaxSpeed;
        s.Acceleration = g.Acceleration; s.Deceleration = g.Deceleration;
        s.DefaultSpeed = g.DefaultSpeed; s.AutoStopWhenEmpty = g.AutoStopWhenEmpty;
    }

    /// <summary>
    /// Propaga il padre a tutti i moduli non personalizzati. Con applyPitch il passo rulli del padre viene riapplicato
    /// (stessa posizione e lunghezza del modulo, cambia il numero di rulli). Restituisce true se ha cambiato qualcosa.
    /// </summary>
    public static bool Propagate(LineProject p, bool applyPitch)
    {
        bool changed = false;
        var g = p.SectionDefaults;
        foreach (var s in p.Sections.Where(x => !x.OverrideGroup))
        {
            var before = (s.WidthMm, s.RollerDiameterMm, s.LowSpeed, s.HighSpeed, s.MaxSpeed, s.Acceleration, s.Deceleration, s.DefaultSpeed, s.AutoStopWhenEmpty);
            CopyDrive(g, s);
            if (before != (s.WidthMm, s.RollerDiameterMm, s.LowSpeed, s.HighSpeed, s.MaxSpeed, s.Acceleration, s.Deceleration, s.DefaultSpeed, s.AutoStopWhenEmpty))
                changed = true;
            if (applyPitch && Math.Abs(s.RollerPitchMm - g.RollerPitchMm) > 1e-6)
            {
                s.RollerPitchMm = LineReflow.RoundPitch(g.RollerPitchMm);
                LineReflow.Fit(s, s.StartMm, s.EndMm);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Diametro dei rulli impostato sul padre: vale per tutti i moduli (poi ogni modulo può cambiarlo).</summary>
    public static void SetDiameterForAll(LineProject p)
    {
        foreach (var s in p.Sections) s.RollerDiameterMm = p.SectionDefaults.RollerDiameterMm;
    }

    /// <summary>Quota piano rulli: il valore del padre è quello del layout (una sola quota per tutta la linea).</summary>
    public static void SyncElevation(LineProject p, bool fromGroup)
    {
        if (fromGroup) p.Layout.RollerTopElevationMm = p.SectionDefaults.RollerTopElevationMm;
        else p.SectionDefaults.RollerTopElevationMm = p.Layout.RollerTopElevationMm;
    }

    /// <summary>
    /// Progetti precedenti: i moduli con parametri diversi dal padre di default restano come sono e vengono marcati "personalizzati".
    /// Si applica solo quando il padre è ancora quello di default (progetto appena caricato).
    /// </summary>
    public static bool MarkCustomized(LineProject p)
    {
        var g = p.SectionDefaults;
        bool changed = false;
        foreach (var s in p.Sections.Where(x => !x.OverrideGroup))
        {
            bool same = Math.Abs(s.WidthMm - g.WidthMm) < 1e-6
                && Math.Abs(s.LowSpeed - g.LowSpeed) < 1e-6 && Math.Abs(s.HighSpeed - g.HighSpeed) < 1e-6
                && Math.Abs(s.MaxSpeed - g.MaxSpeed) < 1e-6 && Math.Abs(s.Acceleration - g.Acceleration) < 1e-6
                && Math.Abs(s.Deceleration - g.Deceleration) < 1e-6 && s.DefaultSpeed == g.DefaultSpeed && s.AutoStopWhenEmpty == g.AutoStopWhenEmpty;
            if (!same) { s.OverrideGroup = true; changed = true; }
        }
        return changed;
    }
}
