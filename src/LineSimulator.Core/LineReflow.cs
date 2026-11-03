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
}
