using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Via del trasferitore su due inverter in double banking (decisione dell'utente del 2026-11-14: nessuno strisciamento).
/// Con un solo inverter i rulli girerebbero sotto il primo pacco, fermo contro il fermo finale, mentre il secondo si
/// posiziona. La via si divide quindi in due parti: a valle (dal punto di divisione al fermo finale) sta il primo pacco,
/// a monte il secondo. La via resta un solo modulo con gli stessi rulli e la stessa lunghezza
/// (<see cref="RollerSection.SplitAtMm"/>): l'inverter di valle tiene il nome del modulo, quello di monte ha il suffisso "a".
/// Dove si divide (sempre a metà tra due rulli):
/// - con un fermo scomparente e intestatura di testa: nello spazio tra i rulli in cui sta il fermo, che è tra i due pacchi;
/// - con un fermo scomparente e intestatura di coda: nello spazio libero tra la testa del secondo (con l'extra corsa) e la
///   coda del primo (2026-11-18);
/// - altrimenti subito dopo l'ultimo rullo sotto la testa del secondo pacco, fermo dietro al primo pacco più lungo in
///   double banking.
/// Un pacco "sta" su una parte quando copre almeno un suo rullo: il primo non deve coprire rulli di monte, il secondo non
/// deve coprire rulli di valle (<see cref="FirstTailMinMm"/>, <see cref="SecondHeadMaxMm"/>).
/// Se nessuna tipologia è in double banking la divisione automatica si toglie; quella fatta a mano non si tocca.
/// </summary>
/// Con il double banking anche il trasferitore sollevabile ha un doppio comando, diviso nello stesso punto: solleva e trasla
/// i due pacchi insieme; se vanno sullo stesso bilico abbassa prima la metà a monte e pesa, poi la metà a valle e pesa il
/// totale (il secondo peso è per differenza).
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

    /// <summary>Margine tra la fine di un pacco e l'asse del primo rullo dell'altra parte (mm).</summary>
    public const double RollerMarginMm = 10;

    private static double Pitch(RollerSection m) => m.RollerPitchMm > 0 ? m.RollerPitchMm : RollerSection.DefaultPitchMm;

    /// <summary>Metà del passo rulli della via del trasferitore: distanza tra il punto di divisione e i rulli ai suoi lati.</summary>
    public static double HalfPitchMm(LineProject p) => LayoutRules.TransferModule(p) is { } m ? Pitch(m) / 2 : 0;

    /// <summary>Coda minima del primo pacco perché non copra rulli di monte (oltre l'ultimo rullo di monte).</summary>
    public static double FirstTailMinMm(LineProject p, double splitMm) => splitMm - HalfPitchMm(p) + RollerMarginMm;

    /// <summary>Testa massima del secondo pacco perché non copra rulli di valle (prima del primo rullo di valle).</summary>
    public static double SecondHeadMaxMm(LineProject p, double splitMm) => splitMm + HalfPitchMm(p) - RollerMarginMm;

    /// <summary>
    /// Mezzeria dello spazio tra rulli subito dopo l'ultimo rullo con l'asse entro <paramref name="xMm"/> (gli assi stanno a
    /// mezzo passo dall'inizio del modulo, poi a ogni passo); 0 se prima di <paramref name="xMm"/> non c'è nessun rullo.
    /// </summary>
    public static double GapAfterLastRollerMm(RollerSection m, double xMm)
    {
        double pitch = Pitch(m);
        double j = Math.Floor((xMm - m.StartMm - pitch / 2) / pitch + 1e-9);
        return j < 0 ? 0 : m.StartMm + (j + 1) * pitch;
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
            x = GapAfterLastRollerMm(m, stop.PositionMm);
            why = $"fermo scomparente {stop.Id}";
        }
        else if (stop != null)
        {
            // Intestatura di coda (decisione dell'utente del 2026-11-18): il secondo pacco sta oltre il fermo scomparente
            // (coda sul fermo, testa più l'extra corsa a valle). Via e doppio comando del trasferitore si dividono nello
            // spazio libero tra la testa del secondo e la coda del primo, dove nessun pacco sta a cavallo: dietro al pacco
            // a monte più lungo per cui resta posto, davanti, almeno per il pacco più corto
            double shortest = dbl.Min(t => t.LengthMm);
            x = 0;
            why = "";
            foreach (var t in dbl.OrderByDescending(t => t.LengthMm))
            {
                double reach = stop.PositionMm + t.LengthMm + stop.TailOvershootMm;
                double gx = GapAfterLastRollerMm(m, reach + RollerMarginMm);
                if (gx <= m.StartMm + 1 || SecondHeadMaxMm(p, gx) < reach - 1e-6) continue;
                if (stopper - shortest < FirstTailMinMm(p, gx) - 1e-6) continue;
                x = gx;
                why = $"intestatura di coda sul fermo scomparente {stop.Id}: nello spazio libero tra i due pacchi, dietro al pacco a monte da {t.LengthMm:0} mm";
                break;
            }
            if (x <= 0) return (0, "");
        }
        else
        {
            // dietro al primo pacco più lungo tra quelli che in due ci stanno (sbalzo ammesso a monte incluso)
            double room = stopper - (start - (stop == null ? p.Transfer.EffectiveMaxOverhangMm : 0));
            double gap = stop == null ? p.Transfer.DoubleBankingGapMm : BankingOptions.TailOvershootClearanceMm;
            var fit = dbl.Where(t => 2 * t.LengthMm + gap <= room + 1e-6).ToList();
            if (fit.Count == 0) return (0, "");
            double longest = fit.Max(t => t.LengthMm);
            x = GapAfterLastRollerMm(m, stopper - longest - gap - RollerMarginMm);
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
            done.Add($"Via del trasferitore divisa in due inverter a {now:0} mm ({why}): il primo pacco resta fermo in battuta mentre il secondo si posiziona, senza strisciamenti. Inverter di valle {down} ({p.Stopper.PositionMm - now:0} mm), di monte {up} ({now - m.StartMm:0} mm). Il trasferitore sollevabile ha il doppio comando nello stesso punto.");
        else if (now > 0)
            done.Add($"Divisione della via del trasferitore spostata da {was:0} a {now:0} mm ({why}). Inverter di valle {down}, di monte {up}.");
        else
            done.Add("Via del trasferitore riunita su un solo inverter: nessuna tipologia di pacco è in double banking.");
        return done;
    }
}
