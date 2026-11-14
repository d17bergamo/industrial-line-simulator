using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Via del trasferitore su due inverter in double banking (decisione dell'utente del 2026-11-14: nessuno strisciamento).
/// Con un solo inverter i rulli girerebbero sotto il primo pacco, fermo contro il fermo finale, mentre il secondo si
/// posiziona. La via si divide quindi in due parti: a valle (dal punto di divisione al fermo finale) sta il primo pacco,
/// a monte il secondo. La via resta un solo modulo con gli stessi rulli e la stessa lunghezza
/// (<see cref="RollerSection.SplitAtMm"/>): l'inverter di valle tiene il nome del modulo, quello di monte ha il suffisso "a".
/// Dove si divide:
/// - con un fermo scomparente e intestatura di testa: sul fermo, che sta tra i due pacchi (tra due rulli, a valle del fermo);
/// - altrimenti dietro al primo pacco più lungo in double banking (distanza del double banking, tra due rulli verso monte):
///   il secondo pacco si ferma con la testa sul punto di divisione.
/// Se nessuna tipologia è in double banking la divisione automatica si toglie; quella fatta a mano non si tocca.
/// </summary>
public static class TransferSplitRule
{
    /// <summary>Suffisso dell'inverter di monte.</summary>
    public const string UpstreamSuffix = "a";

    /// <summary>Punto di divisione attuale della via del trasferitore (mm); 0 = un solo inverter.</summary>
    public static double SplitMm(LineProject p)
    {
        var m = LayoutRules.TransferModule(p);
        return m != null && m.SplitAtMm > m.StartMm + 1 && m.SplitAtMm < m.EndMm - 1 ? m.SplitAtMm : 0;
    }

    /// <summary>Vero se la divisione attuale è stata fatta a mano dall'utente (il programma non la sposta e non la toglie).</summary>
    public static bool IsManual(LineProject p) => LayoutRules.TransferModule(p) is { SplitManual: true } && SplitMm(p) > 0;

    /// <summary>
    /// Punto di divisione da usare per verificare il double banking di <paramref name="t"/>: quello fatto a mano, oppure
    /// quello che il programma metterebbe con <paramref name="t"/> in double banking (0 = la via non si può dividere).
    /// </summary>
    public static double SplitFor(LineProject p, PackageType t) => IsManual(p) ? SplitMm(p) : DesiredMm(p, t);

    /// <summary>Spazio tra due rulli (mezzeria) più vicino a <paramref name="xMm"/>, verso valle o verso monte.</summary>
    public static double RollerGapMm(RollerSection m, double xMm, bool downstream)
    {
        double pitch = m.RollerPitchMm > 0 ? m.RollerPitchMm : RollerSection.DefaultPitchMm;
        double k = (xMm - m.StartMm) / pitch;
        return m.StartMm + (downstream ? Math.Ceiling(k - 1e-6) : Math.Floor(k + 1e-6)) * pitch;
    }

    /// <summary>
    /// Punto di divisione che serve alle tipologie in double banking (più <paramref name="also"/>, se indicata); 0 se nessuna
    /// è in double banking o se la via non si può dividere con il primo tutto a valle e il secondo tutto a monte.
    /// </summary>
    public static double DesiredMm(LineProject p, PackageType? also = null) => Desired(p, also).Mm;

    private static (double Mm, string Why) Desired(LineProject p, PackageType? also)
    {
        var m = LayoutRules.TransferModule(p);
        if (m == null) return (0, "");
        var dbl = p.PackageTypes.Where(t => t == also || BankingOptions.ChosenBanking(p, t) == AccumulationMode.DoubleBanking).ToList();
        if (dbl.Count == 0) return (0, "");
        double stopper = p.Stopper.PositionMm, start = p.Transfer.PositionMm;
        var stop = p.IntermediateStops.Where(s => s.IsEnabled && s.PositionMm > m.StartMm + 1 && s.PositionMm < stopper - 1)
            .OrderByDescending(s => s.PositionMm).FirstOrDefault();
        bool head = dbl.Any(t => BankingOptions.ChosenHeading(p, t) == StopReference.Head);
        double x;
        string why;
        if (stop != null && head)
        {
            x = RollerGapMm(m, stop.PositionMm, downstream: true);
            why = $"fermo scomparente {stop.Id}";
        }
        else
        {
            // dietro al primo pacco più lungo tra quelli che in due ci stanno (sbalzo ammesso a monte incluso)
            double room = stopper - (start - (stop == null ? p.Transfer.EffectiveMaxOverhangMm : 0));
            double gap = stop == null ? p.Transfer.DoubleBankingGapMm : BankingOptions.TailOvershootClearanceMm;
            var fit = dbl.Where(t => 2 * t.LengthMm + gap <= room + 1e-6).ToList();
            if (fit.Count == 0) return (0, "");
            double longest = fit.Max(t => t.LengthMm);
            x = RollerGapMm(m, stopper - longest - gap, downstream: false);
            why = $"dietro al primo pacco da {longest:0} mm";
        }
        if (x <= m.StartMm + 1 || x >= stopper - 1) return (0, "");
        return (x, why);
    }

    /// <summary>
    /// Mette, sposta o toglie la divisione automatica e riporta le scelte dei pacchi a quelle possibili con quella divisione
    /// (<see cref="BankingOptions.NormalizeChanges"/>). Restituisce la descrizione di ciò che ha cambiato.
    /// </summary>
    public static List<string> Apply(LineProject p)
    {
        var done = new List<string>();
        var m = LayoutRules.TransferModule(p);
        if (m == null) { done.AddRange(BankingOptions.NormalizeChanges(p)); return done; }
        if (m.SplitManual && SplitMm(p) <= 0) m.SplitManual = false;      // "a mano" vale solo per una divisione che c'è
        double was = SplitMm(p);
        if (IsManual(p)) { done.AddRange(BankingOptions.NormalizeChanges(p)); return done; }

        // le scelte non possibili tornano a single; la divisione si calcola sulle tipologie rimaste in double banking
        done.AddRange(BankingOptions.NormalizeChanges(p));
        var (now, why) = Desired(p, null);
        m.SplitAtMm = now;
        if (Math.Abs(now - was) < 0.5) return done;
        string down = m.Id, up = m.Id + UpstreamSuffix;
        if (now > 0 && was <= 0)
            done.Add($"Via del trasferitore divisa in due inverter a {now:0} mm ({why}): il primo pacco resta fermo in battuta mentre il secondo si posiziona, senza strisciamenti. Inverter di valle {down} ({p.Stopper.PositionMm - now:0} mm), di monte {up} ({now - m.StartMm:0} mm).");
        else if (now > 0)
            done.Add($"Divisione della via del trasferitore spostata da {was:0} a {now:0} mm ({why}). Inverter di valle {down}, di monte {up}.");
        else
            done.Add("Via del trasferitore riunita su un solo inverter: nessuna tipologia di pacco è in double banking.");
        return done;
    }
}
