using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Quando si tolgono o si inseriscono vie a rulli gli equipaggiamenti si spostano con la linea,
/// in modo che le vie rimaste restino ricollegate tra loro.
/// </summary>
public static class LineReflow
{
    /// <summary>Una via a rulli [startMm, endMm) è stata tolta: tutto ciò che sta a valle arretra della sua lunghezza,
    /// ciò che stava sopra di essa va sull'inizio del punto di giunzione.</summary>
    public static void CloseGap(LineProject p, double startMm, double endMm)
    {
        double delta = endMm - startMm;
        if (delta <= 0) return;
        double Map(double v) => v >= endMm - 1e-6 ? v - delta : v > startMm ? startMm : v;
        Apply(p, Map);
    }

    /// <summary>È stata inserita una via a rulli lunga lengthMm al punto atMm: tutto ciò che sta a valle avanza.</summary>
    public static void OpenGap(LineProject p, double atMm, double lengthMm)
    {
        if (lengthMm <= 0) return;
        Apply(p, v => v >= atMm - 1e-6 ? v + lengthMm : v);
    }

    /// <summary>Le vie a rulli si susseguono senza vuoti né sovrapposizioni, partendo dalla prima.</summary>
    public static IEnumerable<(RollerSection A, RollerSection B, double GapMm)> Discontinuities(LineProject p)
    {
        var ord = p.Sections.OrderBy(s => s.StartMm).ToList();
        for (int i = 1; i < ord.Count; i++)
        {
            double gap = ord[i].StartMm - ord[i - 1].EndMm;
            if (Math.Abs(gap) > 0.5) yield return (ord[i - 1], ord[i], gap);
        }
    }

    private static void Apply(LineProject p, Func<double, double> map)
    {
        foreach (var s in p.Sections) s.StartMm = map(s.StartMm);
        foreach (var x in p.Photocells) x.PositionMm = map(x.PositionMm);
        foreach (var x in p.Strappers) x.PositionMm = map(x.PositionMm);
        foreach (var x in p.IntermediateStops) x.PositionMm = map(x.PositionMm);
        foreach (var x in p.Scales) x.StartMm = map(x.StartMm);
        p.Transfer.PositionMm = map(p.Transfer.PositionMm);
        // 0 = posizione automatica: non si tocca
        if (p.BundleClamp.PositionMm > 0) p.BundleClamp.PositionMm = map(p.BundleClamp.PositionMm);
        if (p.Compactor.PositionMm > 0) p.Compactor.PositionMm = map(p.Compactor.PositionMm);
        if (p.Rejector.PositionMm > 0) p.Rejector.PositionMm = map(p.Rejector.PositionMm);
        if (p.StrappingEncoder.PositionMm > 0) p.StrappingEncoder.PositionMm = map(p.StrappingEncoder.PositionMm);
        ProjectNormalizer.AlignStopperToLastRoller(p);
    }

    /// <summary>Sposta di dxMm tutto ciò che sta oltre fromMm (vie, fotocellule, macchine, trasferitore, bilici): usato per spostare la parte a valle.</summary>
    public static void ShiftBeyond(LineProject p, double fromMm, double dxMm)
    {
        if (Math.Abs(dxMm) < 1e-9) return;
        Apply(p, v => v > fromMm + 1e-6 ? v + dxMm : v);
    }

    /// <summary>
    /// Porta i vecchi valori di default (passo 300 mm, diametro 150 mm) ai nuovi (passo 1250 mm, diametro 268 mm): le vie
    /// mantengono posizione e lunghezza, cambia il numero di rulli. Le vie con valori personalizzati non si toccano.
    /// </summary>
    public static bool ApplyDefaultRollerGeometry(LineProject p)
    {
        bool changed = false;
        foreach (var s in p.Sections)
        {
            if (Math.Abs(s.RollerDiameterMm - 150) > 0.01 || s.RollerPitchMm > 400) continue;
            s.RollerDiameterMm = RollerSection.DefaultDiameterMm;
            s.RollerPitchMm = RollerSection.DefaultPitchMm;
            Fit(s, s.StartMm, s.EndMm);
            changed = true;
        }
        return changed;
    }

    /// <summary>Il passo rulli si arrotonda a multipli di 50 mm.</summary>
    public const double PitchStepMm = 50;

    public static double RoundPitch(double pitchMm) => Math.Max(PitchStepMm, Math.Round(pitchMm / PitchStepMm) * PitchStepMm);

    /// <summary>
    /// Adatta una via a rulli a [start, end): la sporgenza terminale è metà passo, quindi la lunghezza vale sempre
    /// numero rulli × passo. Cerca il numero di rulli per cui il passo è un multiplo di 50 mm il più vicino a quello attuale;
    /// se la lunghezza non lo permette il passo viene ricalcolato (così le giunzioni restano esatte).
    /// </summary>
    public static void Fit(RollerSection s, double startMm, double endMm)
    {
        double pitch = Math.Max(10, s.RollerPitchMm);
        double len = Math.Max(2 * pitch, endMm - startMm);
        int n = Math.Max(2, (int)Math.Round(len / pitch));
        double best = double.MaxValue; int bestN = -1;
        for (int k = Math.Max(2, n / 2); k <= n * 2 + 2; k++)
        {
            double pk = len / k;
            if (Math.Abs(pk / PitchStepMm - Math.Round(pk / PitchStepMm)) > 1e-9) continue;     // passo non multiplo di 50
            double d = Math.Abs(pk - pitch);
            if (d < best) { best = d; bestN = k; }
        }
        if (bestN > 0 && best <= pitch * 0.5) n = bestN;
        s.RollerCount = n;
        s.RollerPitchMm = len / n;
        s.StartMm = startMm;
        s.LengthMm = len;
    }

    /// <summary>Porta tutte le vie a sporgenza = metà passo mantenendo posizione e lunghezza: cambia solo il passo.</summary>
    public static bool EnforceOverhang(LineProject p)
    {
        bool changed = false;
        foreach (var s in p.Sections)
        {
            if (Math.Abs(s.LengthMm - s.RollerCount * s.RollerPitchMm) < 0.01) continue;
            Fit(s, s.StartMm, s.EndMm);
            changed = true;
        }
        return changed;
    }

    /// <summary>Lunghezza minima di una via adattata: due rulli (2 × passo, sporgenze comprese).</summary>
    public static double MinLengthMm(RollerSection s) => 2 * Math.Max(10, s.RollerPitchMm);

    /// <summary>
    /// Sposta una via di dxMm tenendo unite le vie vicine: quella a monte si allunga/accorcia e quella a valle idem.
    /// Se è l'ultima via, con lei si spostano anche trasferitore e bilici (il fermo finale la segue da solo).
    /// Restituisce lo spostamento effettivo (limitato dalle lunghezze minime delle vie vicine).
    /// </summary>
    public static double MoveSection(LineProject p, RollerSection s, double dxMm)
    {
        var ord = p.Sections.OrderBy(x => x.StartMm).ToList();
        int i = ord.IndexOf(s);
        var prev = i > 0 ? ord[i - 1] : null;
        var next = i < ord.Count - 1 ? ord[i + 1] : null;
        double dx = dxMm;
        if (prev != null) dx = Math.Max(dx, prev.StartMm + MinLengthMm(prev) - s.StartMm);
        if (next != null) dx = Math.Min(dx, next.EndMm - MinLengthMm(next) - s.EndMm);
        if (Math.Abs(dx) < 1e-9) return 0;
        // ciò che sta dentro la via (fotocellule, macchine, fermi) si sposta con lei; l'ultima fotocellula prima del fermo
        // finale segue il fermo (spostata da AlignStopperToLastRoller)
        var stopperPc = ProjectNormalizer.StopperPhotocell(p);
        bool InS(double v) => v >= s.StartMm - 1e-6 && v < s.EndMm - 1e-6;
        foreach (var x in p.Photocells.Where(x => !x.IsEntryPhotocell && x != stopperPc && InS(x.PositionMm))) x.PositionMm += dx;
        foreach (var x in p.Strappers.Where(x => InS(x.PositionMm))) x.PositionMm += dx;
        if (next != null)
        {
            foreach (var x in p.IntermediateStops.Where(x => InS(x.PositionMm))) x.PositionMm += dx;
            foreach (var x in p.Scales.Where(x => InS(x.StartMm))) x.StartMm += dx;
            if (InS(p.Transfer.PositionMm)) p.Transfer.PositionMm += dx;
        }
        if (p.BundleClamp.PositionMm > 0 && InS(p.BundleClamp.PositionMm)) p.BundleClamp.PositionMm += dx;
        if (p.Compactor.PositionMm > 0 && InS(p.Compactor.PositionMm)) p.Compactor.PositionMm += dx;
        if (p.Rejector.PositionMm > 0 && InS(p.Rejector.PositionMm)) p.Rejector.PositionMm += dx;
        if (p.StrappingEncoder.PositionMm > 0 && InS(p.StrappingEncoder.PositionMm)) p.StrappingEncoder.PositionMm += dx;
        double ns = s.StartMm + dx, ne = s.EndMm + dx;
        double nextEnd = next?.EndMm ?? 0, prevStart = prev?.StartMm ?? 0;
        s.StartMm = ns;
        if (prev != null) Fit(prev, prevStart, ns);
        if (next != null) Fit(next, ne, nextEnd);
        if (next == null)
        {
            p.Transfer.PositionMm += dx;
            foreach (var sc in p.Scales) sc.StartMm += dx;
            foreach (var fs in p.IntermediateStops) fs.PositionMm += dx;
        }
        ProjectNormalizer.AlignStopperToLastRoller(p, stopperPc);
        return dx;
    }

    /// <summary>Una via ha cambiato lunghezza (numero rulli o passo): ciò che sta a valle segue per restare unito.</summary>
    public static void AfterResize(LineProject p, RollerSection s, double oldEndMm)
    {
        double delta = s.EndMm - oldEndMm;
        if (Math.Abs(delta) < 1e-9) return;
        double Map(double v) => v >= oldEndMm - 1e-6 ? v + delta : v;
        var stopperPc = ProjectNormalizer.StopperPhotocell(p);     // segue il fermo finale (spostata da AlignStopperToLastRoller)
        foreach (var o in p.Sections.Where(x => !ReferenceEquals(x, s))) o.StartMm = Map(o.StartMm);
        foreach (var x in p.Photocells.Where(x => x != stopperPc)) x.PositionMm = Map(x.PositionMm);
        foreach (var x in p.Strappers) x.PositionMm = Map(x.PositionMm);
        foreach (var x in p.IntermediateStops) x.PositionMm = Map(x.PositionMm);
        foreach (var x in p.Scales) x.StartMm = Map(x.StartMm);
        p.Transfer.PositionMm = Map(p.Transfer.PositionMm);
        ProjectNormalizer.AlignStopperToLastRoller(p, stopperPc);
    }
}
