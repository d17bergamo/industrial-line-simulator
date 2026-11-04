using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Quote di un equipaggiamento nel sistema di riferimento unico del layout (metri, origine X = Y = Z = 0).</summary>
public sealed record PlacedInfo(string Id, string Name, string Kind, double CenterXm, double EntryXm, double ExitXm, double YM, double ZM);

/// <summary>
/// Coordinate degli equipaggiamenti calcolate dalla geometria effettiva. I punti di ingresso e uscita sono definiti nel
/// sistema locale della macchina (asse X locale = asse linea) e trasformati nel sistema globale con <see cref="ToGlobal"/>.
/// </summary>
public static class LayoutGeometry
{
    /// <summary>Trasforma un punto locale (lx, ly) di una macchina centrata in (cx, cy) con rotazione yaw (gradi, attorno a Z).</summary>
    public static (double X, double Y) ToGlobal(double cx, double cy, double yawDeg, double lx, double ly)
    {
        double a = yawDeg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
        return (cx + lx * c - ly * s, cy + lx * s + ly * c);
    }

    public static double LineWidthM(LineProject p) => p.Sections.Count > 0 ? p.Sections.Max(s => s.WidthMm) / 1000.0 : 1.2;

    private static PlacedInfo Make(string id, string name, string kind, double centerXm, double lengthM, double ym, double zm, double yawDeg = 0)
    {
        var entry = ToGlobal(centerXm, ym, yawDeg, -lengthM / 2, 0);
        var exit = ToGlobal(centerXm, ym, yawDeg, lengthM / 2, 0);
        return new PlacedInfo(id, name, kind, centerXm, entry.X, exit.X, ym, zm);
    }

    public static IReadOnlyList<PlacedInfo> Describe(LineProject p)
    {
        var list = new List<PlacedInfo>();
        double w = LineWidthM(p), zTop = p.Layout.RollerTopElevationMm / 1000.0;
        foreach (var s in p.Sections.OrderBy(x => x.StartMm))
            list.Add(Make(s.Id, s.Name, "Via a rulli", (s.StartMm + s.LengthMm / 2) / 1000.0, s.LengthMm / 1000.0, 0, zTop));
        foreach (var st in p.Strappers)
            list.Add(Make(st.Id, st.Name, "Reggiatrice", st.PositionMm / 1000.0, Math.Max(300, st.FootprintLengthMm) / 1000.0,
                (st.Side == LateralSide.Left ? 1 : -1) * (w / 2 + 0.45), 0));
        if (p.BundleClamp.IsEnabled)
            list.Add(Make(p.BundleClamp.Id, p.BundleClamp.Name, "Stringifascio KNOF", p.BundleClamp.PositionMm / 1000.0, 0.3, 0, 0));
        if (p.Compactor.IsEnabled)
            list.Add(Make(p.Compactor.Id, p.Compactor.Name, "Compattatore laterale", p.Compactor.PositionMm / 1000.0, Math.Max(300, p.Compactor.JawLengthMm) / 1000.0, 0, zTop));
        if (p.Rejector.IsEnabled)
            list.Add(Make(p.Rejector.Id, p.Rejector.Name, "Rigettatrice", (p.Rejector.PositionMm + p.Rejector.LengthMm / 2) / 1000.0, p.Rejector.LengthMm / 1000.0, 0, zTop));
        foreach (var f in p.IntermediateStops)
            list.Add(Make(f.Id, f.Name, "Fermo scomparente", f.PositionMm / 1000.0 + 0.05, 0.1, 0, zTop));
        list.Add(Make(p.Stopper.Id, p.Stopper.Name, "Stopper", p.Stopper.PositionMm / 1000.0 + 0.06, 0.12, 0, zTop));
        list.Add(Make(p.Transfer.Id, p.Transfer.Name, "Trasferitore", (p.Transfer.PositionMm + p.Transfer.LengthMm / 2) / 1000.0, p.Transfer.LengthMm / 1000.0, 0, zTop));
        foreach (var b in p.Scales)
            list.Add(Make(b.Id, b.Name, "Bilico", (b.StartMm + b.LengthMm / 2) / 1000.0, b.LengthMm / 1000.0,
                (p.Transfer.DestinationSide == LateralSide.Left ? 1 : -1) * p.Transfer.TranslationStrokeMm / 1000.0, zTop));
        foreach (var f in p.Photocells)
            list.Add(Make(f.Id, f.Name, "Fotocellula", f.PositionMm / 1000.0, 0.06, 0, zTop));
        return list;
    }

    /// <summary>Quota (m) della fotocellula di ingresso, origine delle quote relative; 0 se non c'è.</summary>
    public static double EntryReferenceM(LineProject p)
    {
        var pc = p.Photocells.FirstOrDefault(f => f.IsEntryPhotocell) ?? p.Photocells.OrderBy(f => f.PositionMm).FirstOrDefault();
        return pc == null ? 0 : pc.PositionMm / 1000.0;
    }

    /// <summary>Distanza tra due macchine contigue sull'asse linea: dall'uscita della prima all'ingresso della seconda e tra i centri.</summary>
    public sealed record MachineGap(string FromId, string ToId, double ClearM, double CenterM);

    private static readonly HashSet<string> LineMachines = new() { "Reggiatrice", "Stringifascio KNOF", "Compattatore laterale", "Rigettatrice", "Stopper", "Trasferitore" };

    /// <summary>Macchine sull'asse linea, ordinate lungo X, e distanze tra macchine adiacenti (reggiatrici su lati opposti comprese).</summary>
    public static IReadOnlyList<MachineGap> Gaps(LineProject p)
    {
        var m = Describe(p).Where(i => LineMachines.Contains(i.Kind)).OrderBy(i => i.CenterXm).ToList();
        var gaps = new List<MachineGap>();
        for (int i = 1; i < m.Count; i++)
            gaps.Add(new MachineGap(m[i - 1].Id, m[i].Id, m[i].EntryXm - m[i - 1].ExitXm, m[i].CenterXm - m[i - 1].CenterXm));
        return gaps;
    }
}
