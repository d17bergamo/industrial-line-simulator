using System.Globalization;
using System.Text;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Models;

namespace LineSimulator.Infrastructure;

/// <summary>
/// PDF della vista dall'alto della linea (A4 orizzontale): moduli via rulli colorati per sezione inverter, motori commutabili,
/// legatrici con il lato, fotocellule, encoder, fermi, trasferitore e bilici; sotto, le catene di quote
/// (fotocellule tra loro, fotocellule verso fermi e trasferitore, area di legatura, sezioni inverter).
/// Seguono le tabelle con le distanze esatte. Il disegno usa il progetto come lo vede la simulazione
/// (stesse correzioni automatiche dell'avvio), senza modificare il progetto aperto.
/// </summary>
public static class LayoutTopViewPdf
{
    private const double PageW = 842, PageH = 595, Margin = 36;
    private const double BandY = 360, BandH = 40;            // fascia della via a rulli (y del bordo inferiore e altezza)

    private static readonly (double R, double G, double B)[] Palette =
    {
        (0.33, 0.56, 0.85), (0.95, 0.62, 0.20), (0.40, 0.72, 0.45), (0.78, 0.45, 0.75),
        (0.90, 0.40, 0.40), (0.30, 0.70, 0.75), (0.75, 0.68, 0.30), (0.55, 0.55, 0.80)
    };

    public static void Save(string path, LineProject project) => File.WriteAllBytes(path, Build(project));

    /// <summary>PDF della vista dall'alto. Il progetto non viene modificato: si lavora su una copia preparata.</summary>
    public static byte[] Build(LineProject source)
    {
        var p = ProjectSerializer.Clone(source);
        try { PhotocellRules.BeforeSimulation(p); SimulationPreparation.Prepare(p); }
        catch { /* progetto incompleto: si disegna com'è */ }
        string title = string.IsNullOrWhiteSpace(p.Name) ? "Linea di legatura" : p.Name;
        var pages = new List<string> { PlanPage(p, title) };
        pages.AddRange(TablePages(p, title));
        return StepCyclogramPdf.Assemble(pages);
    }

    // ================================================================== dati comuni

    public sealed record Zone(string Id, string Label, List<RollerSection> Sections, double StartMm, double EndMm, (double R, double G, double B) Color);

    public static List<Zone> Zones(LineProject p)
    {
        var secs = p.Sections.OrderBy(s => s.StartMm).ToList();
        var zones = new List<Zone>();
        foreach (var s in secs)
        {
            var z = zones.FirstOrDefault(x => x.Id == s.EffectiveZoneId);
            if (z == null)
            {
                z = new Zone(s.EffectiveZoneId, "", new List<RollerSection>(), s.StartMm, s.EndMm, Palette[zones.Count % Palette.Length]);
                zones.Add(z);
            }
            z.Sections.Add(s);
        }
        return zones.Select(z => z with
        {
            StartMm = z.Sections.Min(s => s.StartMm),
            EndMm = z.Sections.Max(s => s.EndMm),
            Label = "Inverter " + Short(z.Sections[0]) + ": " + string.Join(" + ", z.Sections.Select(Nm))
        }).ToList();
    }

    /// <summary>Elementi di arresto lungo la linea: fermo finale, fermi scomparenti, inizio del trasferitore.</summary>
    internal static List<(string Name, double PosMm)> Stops(LineProject p)
    {
        var list = new List<(string, double)>();
        if (p.Sections.Count > 0) list.Add(("Fermo finale", p.Stopper.PositionMm));
        foreach (var s in p.IntermediateStops.Where(s => s.IsEnabled)) list.Add(("Fermo scomparente " + Nm(s), s.PositionMm));
        if (p.Transfer.LengthMm > 0) list.Add(("Inizio trasferitore", p.Transfer.PositionMm));
        return list.OrderBy(x => x.Item2).ToList();
    }

    private static string Nm(ComponentBase c) => string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name;
    /// <summary>Sigla breve per il disegno (Id), il nome completo va nelle tabelle.</summary>
    private static string Short(ComponentBase c) => string.IsNullOrWhiteSpace(c.Id) ? c.Name : c.Id;

    private static string Purpose(PhotocellConfig pc)
    {
        if (pc.IsEntryPhotocell) return "Ingresso (identificazione)";
        var s = pc.Purpose switch
        {
            PhotocellPurpose.TransferStop => "Arresto davanti al trasferitore",
            PhotocellPurpose.StrappingReference => "Riferimento encoder di legatura",
            PhotocellPurpose.StrappingExit => "Uscita via di legatura",
            _ => pc.Action switch
            {
                PhotocellAction.SlowDown => "Rallentamento",
                PhotocellAction.Stop => "Arresto",
                PhotocellAction.Accumulation => "Accumulo",
                PhotocellAction.SpeedUp => "Velocità alta",
                _ => "Segnale"
            }
        };
        return pc.AlsoTransferStop ? s + " + arresto trasferitore" : s;
    }

    // ================================================================== pagina della pianta

    private static string PlanPage(LineProject p, string title)
    {
        var c = new StringBuilder();
        var zones = Zones(p);
        var pcs = p.Photocells.Where(x => x.IsEnabled).OrderBy(x => x.PositionMm).ToList();
        var strappers = p.Strappers.Where(x => x.IsEnabled).OrderBy(x => x.PositionMm).ToList();
        var stops = Stops(p);

        double xMin = new[] { p.Sections.Count > 0 ? p.Sections.Min(s => s.StartMm) : 0, pcs.Count > 0 ? pcs.Min(x => x.PositionMm) : 0 }.Min();
        double xMax = new[]
        {
            p.Sections.Count > 0 ? p.Sections.Max(s => s.EndMm) : 1000, p.Transfer.LengthMm > 0 ? p.Transfer.EndMm : 0,
            p.Stopper.PositionMm, pcs.Count > 0 ? pcs.Max(x => x.PositionMm) : 0,
            p.Scales.Count > 0 ? p.Scales.Max(s => s.StartMm + s.LengthMm) : 0
        }.Max();
        double span = Math.Max(1000, xMax - xMin);
        double plotX0 = Margin + 70, plotW = PageW - plotX0 - Margin;
        double X(double mm) => plotX0 + (mm - xMin) / span * plotW;

        Text(c, Margin, PageH - 36, 14, $"{title} - vista dall'alto");
        Text(c, Margin, PageH - 50, 8,
            $"Flusso da sinistra a destra. Quote lungo la linea in scala (1 : {Math.Round(span / (plotW / 72 * 25.4)):0}), larghezze non in scala. Quote in metri; i valori esatti in mm sono nelle tabelle.");

        // ---------------- legenda delle sezioni inverter
        double lx = Margin, ly = PageH - 68;
        Text(c, lx, ly + 1, 7.5, "Sezioni inverter:");
        lx += 64;
        foreach (var z in zones)
        {
            string lab = z.Label + (z.Sections.Any(s => s.FollowsStrapping) ? " - motori commutabili" : "");
            Fill(c, z.Color); Rect(c, lx, ly, 10, 8, true);
            Gray(c, 0); Text(c, lx + 13, ly + 1, 6.5, lab);
            lx += 22 + lab.Length * 3.3;
            if (lx > PageW - 160) { lx = Margin + 64; ly -= 11; }
        }
        ly -= 12;
        Text(c, Margin, ly, 6.5, "Simboli: linea verticale = fotocellula (rossa = arresto/rallentamento, blu = ingresso/riferimento, viola = uscita legatura, grigia = segnale); barra nera = fermo; rombo blu = encoder.");
        Text(c, Margin, ly - 9, 6.5, "Rettangoli arancio sopra/sotto la via = legatrici sul lato sinistro/destro (tratteggio = piano di legatura); verde = trasferitore; tratteggio verde = bilico; righe oblique = motori commutabili.");

        // ---------------- fascia via rulli: moduli colorati per inverter
        var strappingZoneColor = zones.FirstOrDefault(z => z.Sections.Any(s => StrappingAreaRules.StrappingModule(p)?.Id == s.Id))?.Color ?? (0.95, 0.62, 0.20);
        foreach (var z in zones)
            foreach (var s in z.Sections)
            {
                double xa = X(s.StartMm), xb = X(s.EndMm);
                Fill(c, Light(z.Color)); Rect(c, xa, BandY, xb - xa, BandH, true);
                if (s.FollowsStrapping)
                {
                    // motori commutabili: righe oblique nel colore dell'inverter di legatura
                    Stroke(c, strappingZoneColor); c.Append("1.2 w\n");
                    for (double hx = xa - BandH; hx < xb; hx += 7)
                    {
                        double x1 = Math.Max(xa, hx), y1 = BandY + (x1 - hx);
                        double x2 = Math.Min(xb, hx + BandH), y2 = BandY + (x2 - hx);
                        if (x2 > x1) Line(c, x1, y1, x2, y2);
                    }
                }
                // rulli
                Stroke(c, (0.55, 0.55, 0.55)); c.Append("0.4 w\n");
                for (int k = 0; k < s.RollerCount; k++)
                {
                    double rx = X(s.StartMm + s.EndOverhangMm + k * s.RollerPitchMm);
                    Line(c, rx, BandY + 3, rx, BandY + BandH - 3);
                }
                Stroke(c, z.Color); c.Append("1.5 w\n"); Rect(c, xa, BandY, xb - xa, BandH, false);
                Gray(c, 0);
                string lab = Nm(s) + (s.FollowsStrapping ? " (comm.)" : "");
                if (xb - xa > lab.Length * 3.2 + 4) Text(c, xa + 3, BandY + BandH - 9, 6.5, lab);
            }

        // ---------------- trasferitore e bilici
        if (p.Transfer.LengthMm > 0)
        {
            bool right = p.Transfer.DestinationSide == LateralSide.Right;
            double xa = X(p.Transfer.PositionMm), xb = X(p.Transfer.EndMm);
            double ty = right ? BandY - 46 : BandY + BandH + 4;
            Fill(c, (0.83, 0.93, 0.83)); Rect(c, xa, ty, xb - xa, 42, true);
            Stroke(c, (0.30, 0.65, 0.35)); c.Append("1 w\n"); Rect(c, xa, ty, xb - xa, 42, false);
            Gray(c, 0);
            string kind = p.Transfer.Kind == TransferKind.Lifting ? "sollevabile" : "a catene";
            string tn = Nm(p.Transfer).Contains(kind, StringComparison.OrdinalIgnoreCase) ? Nm(p.Transfer) : $"{Nm(p.Transfer)} ({kind})";
            Text(c, xa + 3, ty + 31, 6.5, $"{tn}, {M(p.Transfer.LengthMm)} m, corsa {M(p.Transfer.TranslationStrokeMm)} m");
            Text(c, xa + 3, ty + 21, 6.5, right ? "scarico verso il lato destro (in basso)" : "scarico verso il lato sinistro (in alto)");
            double ax = (xa + xb) / 2, a0 = right ? ty + 14 : ty + 6, a1 = right ? ty + 4 : ty + 16;
            Stroke(c, (0.30, 0.65, 0.35)); c.Append("1.2 w\n");
            Line(c, ax, a0, ax, a1); Line(c, ax - 3, a1 + (right ? 3 : -3), ax, a1); Line(c, ax + 3, a1 + (right ? 3 : -3), ax, a1);
        }
        foreach (var sc in p.Scales)
        {
            double xa = X(sc.StartMm), xb = X(sc.StartMm + sc.LengthMm);
            Stroke(c, (0.20, 0.45, 0.25)); c.Append("0.8 w [3 2] 0 d\n");
            Rect(c, xa, BandY + 2, xb - xa, BandH - 4, false);
            c.Append("[] 0 d\n"); Gray(c, 0);
            Text(c, xa + 2, BandY + 4, 6, Nm(sc));
        }

        // ---------------- legatrici
        foreach (var st in strappers)
        {
            bool left = st.Side == LateralSide.Left;
            double xa = X(st.PositionMm - st.FootprintLengthMm / 2), xb = X(st.PositionMm + st.FootprintLengthMm / 2);
            double y0 = left ? BandY + BandH + 2 : BandY - 26;
            Fill(c, (0.98, 0.85, 0.65)); Rect(c, xa, y0, Math.Max(3, xb - xa), 24, true);
            Stroke(c, (0.80, 0.45, 0.10)); c.Append("0.8 w\n"); Rect(c, xa, y0, Math.Max(3, xb - xa), 24, false);
            double xs = X(st.PositionMm);
            c.Append("[2 1.5] 0 d\n"); Line(c, xs, BandY, xs, BandY + BandH); c.Append("[] 0 d\n");
            Gray(c, 0); Text(c, xa + 1, y0 + (left ? 26 : -8), 6.5, Short(st) + (left ? " sin." : " des."));
        }

        // ---------------- encoder, fermi
        if (p.StrappingEncoder.IsEnabled)
        {
            double ex = X(p.StrappingEncoder.PositionMm), ey = BandY - 6;
            Fill(c, (0.20, 0.35, 0.80));
            c.Append($"{F(ex)} {F(ey + 4)} m {F(ex + 4)} {F(ey)} l {F(ex)} {F(ey - 4)} l {F(ex - 4)} {F(ey)} l f\n");
            Gray(c, 0); Text(c, ex + 5, ey - 2, 6, "Encoder");
        }
        foreach (var (name, pos) in stops)
        {
            if (name == "Inizio trasferitore") continue;
            double sx = X(pos);
            Gray(c, 0.1); Rect(c, sx - 1.8, BandY - 4, 3.6, BandH + 8, true);
            bool trfBelow = p.Transfer.LengthMm > 0 && p.Transfer.DestinationSide == LateralSide.Right && pos >= p.Transfer.PositionMm - 1 && pos <= p.Transfer.EndMm + 1;
            string sl = name.Replace("Fermo scomparente ", "");
            Text(c, Math.Min(sx - 4, PageW - Margin - sl.Length * 3.1), trfBelow ? BandY - 57 : BandY - 13, 6, sl);
        }

        // ---------------- fotocellule
        int i = 0;
        foreach (var pc in pcs)
        {
            double px = X(pc.PositionMm);
            var col = pc.IsEntryPhotocell || pc.Purpose == PhotocellPurpose.StrappingReference ? (0.15, 0.35, 0.85)
                : pc.Purpose == PhotocellPurpose.StrappingExit ? (0.55, 0.25, 0.70)
                : PhotocellRules.TargetMm(p, pc) != null ? (0.85, 0.15, 0.15) : (0.35, 0.35, 0.35);
            Stroke(c, col); c.Append("1.2 w\n");
            Line(c, px, BandY - 3, px, BandY + BandH + 3);
            double ty = BandY + BandH + 34 + (i % 3) * 9;
            c.Append("0.4 w\n"); Line(c, px, BandY + BandH + 3, px, ty - 1);
            Fill(c, col); Text(c, px - 4, ty + 1, 6.5, Short(pc));
            i++;
        }
        Gray(c, 0);

        // ---------------- catene di quote
        double y = BandY - 70;
        var chainPc = pcs.Select(x => (Short(x), x.PositionMm)).ToList();
        Chain(c, X, y, "Fotocellule tra loro", chainPc); y -= 42;

        var chainStop = pcs.Where(x => PhotocellRules.TargetMm(p, x) != null).Select(x => (Short(x), x.PositionMm))
            .Concat(stops.Select(x => (x.Name.Replace("Fermo scomparente ", "").Replace("Inizio trasferitore", "Trasf."), x.PosMm))).OrderBy(x => x.Item2).ToList();
        Chain(c, X, y, "Fotocellule e fermi", chainStop); y -= 42;

        var chainStr = new List<(string, double)>();
        var refPc = pcs.FirstOrDefault(x => x.Purpose == PhotocellPurpose.StrappingReference);
        var entry = pcs.FirstOrDefault(x => x.IsEntryPhotocell);
        var exit = pcs.FirstOrDefault(x => x.Purpose == PhotocellPurpose.StrappingExit);
        if (entry != null) chainStr.Add((Short(entry), entry.PositionMm));
        if (refPc != null && refPc != entry) chainStr.Add((Short(refPc), refPc.PositionMm));
        if (p.StrappingEncoder.IsEnabled) chainStr.Add(("Enc", p.StrappingEncoder.PositionMm));
        chainStr.AddRange(strappers.Select(s => (Short(s), s.PositionMm)));
        if (exit != null) chainStr.Add((Short(exit), exit.PositionMm));
        Chain(c, X, y, "Area di legatura", chainStr.OrderBy(x => x.Item2).ToList()); y -= 42;

        var chainZone = new List<(string, double)>();
        foreach (var z in zones) { chainZone.Add(("", z.StartMm)); }
        if (zones.Count > 0) chainZone.Add(("", zones[^1].EndMm));
        Chain(c, X, y, "Sezioni inverter", chainZone.DistinctBy(x => Math.Round(x.Item2)).ToList(), zones.Select(z => z.Color).ToList());

        Gray(c, 0.4);
        Text(c, Margin, 22, 6.5, $"Generato dal simulatore il {DateTime.Now:dd/MM/yyyy HH:mm}. Linea da {M(xMin)} a {M(xMax)} m.");
        Gray(c, 0);
        return c.ToString();
    }

    /// <summary>Catena di quote orizzontale: tacche sulle posizioni, valore in metri tra due tacche consecutive.</summary>
    private static void Chain(StringBuilder c, Func<double, double> X, double y, string label, List<(string Name, double PosMm)> pts,
        List<(double R, double G, double B)>? colors = null)
    {
        Gray(c, 0); Text(c, Margin, y - 2, 7, label);
        if (pts.Count < 2) { Gray(c, 0.5); Text(c, Margin + 70, y - 2, 6.5, "-"); Gray(c, 0); return; }
        Stroke(c, (0.25, 0.25, 0.25)); c.Append("0.5 w\n");
        Line(c, X(pts[0].PosMm), y, X(pts[^1].PosMm), y);
        for (int k = 0; k < pts.Count; k++)
        {
            double x = X(pts[k].PosMm);
            Line(c, x, y - 4, x, y + 4);
            Line(c, x - 2, y - 2, x + 2, y + 2);
            if (pts[k].Name.Length > 0) { Gray(c, 0.35); Text(c, Math.Min(x - 3, PageW - Margin - pts[k].Name.Length * 2.9), y - 12 - (k % 2) * 7, 5.5, pts[k].Name); }
        }
        for (int k = 0; k + 1 < pts.Count; k++)
        {
            double d = pts[k + 1].PosMm - pts[k].PosMm;
            if (d < 1) continue;
            double xa = X(pts[k].PosMm), xb = X(pts[k + 1].PosMm);
            string t = M(d);
            double w = t.Length * 3.1;
            double ty = xb - xa >= w + 2 ? y + 3 : y + 3 + ((k % 2) + 1) * 7;
            if (colors != null && k < colors.Count) { Fill(c, colors[k]); Rect(c, xa, y - 1.2, xb - xa, 2.4, true); }
            Gray(c, 0); Text(c, (xa + xb) / 2 - w / 2, ty, 6, t);
        }
        Gray(c, 0);
    }

    // ================================================================== tabelle

    private static IEnumerable<string> TablePages(LineProject p, string title)
    {
        var t = new TableWriter(title + " - vista dall'alto: quote e sezioni");
        var zones = Zones(p);
        var strapModule = StrappingAreaRules.StrappingModule(p);
        var strapZone = strapModule == null ? null : zones.FirstOrDefault(z => z.Sections.Contains(strapModule));

        t.Table("Sezioni inverter", new[] { ("Inverter", 60.0), ("Moduli", 150.0), ("Da [mm]", 55.0), ("A [mm]", 55.0), ("Lunghezza [mm]", 65.0),
            ("V min/nom/max [m/s]", 85.0), ("Acc/Dec [m/s²]", 65.0), ("Note", 235.0) },
            zones.Select(z =>
            {
                var s0 = z.Sections[0];
                var notes = new List<string>();
                if (z == strapZone) notes.Add("via di legatura");
                foreach (var f in z.Sections.Where(s => s.FollowsStrapping))
                    notes.Add($"{Nm(f)}: motori commutabili sull'inverter di legatura{(strapZone != null ? " (" + Nm(strapZone.Sections[0]) + ")" : "")}");
                if (z.Sections.Count > 1) notes.Add("moduli comandati da un solo inverter");
                if (!s0.AutoStopWhenEmpty) notes.Add("senza arresto automatico");
                return new[]
                {
                    "Inverter " + Short(s0), string.Join(" + ", z.Sections.Select(Nm)), Mm(z.StartMm), Mm(z.EndMm), Mm(z.EndMm - z.StartMm),
                    $"{N2(s0.LowSpeed)} / {N2(s0.HighSpeed)} / {N2(s0.MaxSpeed)}", $"{N2(s0.Acceleration)} / {N2(s0.Deceleration)}", string.Join("; ", notes)
                };
            }));

        var pcs = p.Photocells.Where(x => x.IsEnabled).OrderBy(x => x.PositionMm).ToList();
        var strappers = p.Strappers.Where(x => x.IsEnabled).OrderBy(x => x.PositionMm).ToList();
        var stops = Stops(p);
        t.Table("Fotocellule", new[] { ("Sigla", 30.0), ("Nome", 110.0), ("Posizione [mm]", 52.0), ("Funzione", 120.0), ("Dalla precedente", 62.0),
            ("Punto di arresto", 95.0), ("Distanza [mm]", 50.0), ("Richiesta [mm]", 53.0), ("Fermo più vicino a valle", 105.0), ("Legatrice più vicina", 93.0) },
            pcs.Select((pc, k) =>
            {
                double? target = PhotocellRules.TargetMm(p, pc);
                string targetName = target == null ? "-" : NameAt(p, stops, zones, target.Value);
                var nextStop = stops.Where(s => s.PosMm >= pc.PositionMm - 1e-6).Select(s => ((string, double)?)s).FirstOrDefault();
                var nearStr = strappers.OrderBy(s => Math.Abs(s.PositionMm - pc.PositionMm)).FirstOrDefault();
                return new[]
                {
                    Short(pc), Nm(pc), Mm(pc.PositionMm), Purpose(pc), k == 0 ? "-" : Mm(pc.PositionMm - pcs[k - 1].PositionMm),
                    targetName, target == null ? "-" : Mm(target.Value - pc.PositionMm),
                    target == null ? "-" : Mm(PhotocellRules.RequiredDistanceMm(p, pc)),
                    nextStop == null ? "-" : $"{nextStop.Value.Item1}: {Mm(nextStop.Value.Item2 - pc.PositionMm)}",
                    nearStr == null ? "-" : $"{Short(nearStr)}: {SignedMm(nearStr.PositionMm - pc.PositionMm)}"
                };
            }));

        var refPc = pcs.FirstOrDefault(x => x.Purpose == PhotocellPurpose.StrappingReference);
        var exit = pcs.FirstOrDefault(x => x.Purpose == PhotocellPurpose.StrappingExit);
        t.Table("Legatrici", new[] { ("Sigla", 30.0), ("Nome", 90.0), ("Piano di legatura [mm]", 80.0), ("Lato", 40.0), ("Ingombro [mm]", 55.0),
            ("Dalla precedente [mm]", 80.0), ("Da fotocellula di riferimento [mm]", 120.0), ("Da inizio via di legatura [mm]", 105.0), ("A uscita legatura [mm]", 90.0), ("Al trasferitore [mm]", 80.0) },
            strappers.Select((s, k) => new[]
            {
                Short(s), Nm(s), Mm(s.PositionMm), s.Side == LateralSide.Left ? "sinistro" : "destro", Mm(s.FootprintLengthMm),
                k == 0 ? "-" : Mm(s.PositionMm - strappers[k - 1].PositionMm),
                refPc == null ? "-" : Mm(s.PositionMm - refPc.PositionMm),
                strapModule == null ? "-" : Mm(s.PositionMm - strapModule.StartMm),
                exit == null ? "-" : Mm(exit.PositionMm - s.PositionMm),
                p.Transfer.LengthMm > 0 ? Mm(p.Transfer.PositionMm - s.PositionMm) : "-"
            }));

        var others = new List<string[]>();
        if (p.StrappingEncoder.IsEnabled)
            others.Add(new[] { "Encoder di legatura", Mm(p.StrappingEncoder.PositionMm), refPc == null ? "" : $"{Mm(p.StrappingEncoder.PositionMm - refPc.PositionMm)} mm dopo {Nm(refPc)}" });
        foreach (var (name, pos) in stops)
        {
            var before = pcs.LastOrDefault(x => x.PositionMm <= pos + 1e-6);
            others.Add(new[] { name, Mm(pos), before == null ? "" : $"{Mm(pos - before.PositionMm)} mm dopo {Nm(before)}" });
        }
        if (p.Transfer.LengthMm > 0)
            others.Add(new[] { "Fine trasferitore", Mm(p.Transfer.EndMm), $"lunghezza {Mm(p.Transfer.LengthMm)} mm, corsa {Mm(p.Transfer.TranslationStrokeMm)} mm, verso il lato {(p.Transfer.DestinationSide == LateralSide.Right ? "destro" : "sinistro")}" });
        foreach (var sc in p.Scales)
            others.Add(new[] { "Bilico " + Nm(sc), $"{Mm(sc.StartMm)} - {Mm(sc.StartMm + sc.LengthMm)}", $"lunghezza {Mm(sc.LengthMm)} mm" });
        t.Table("Encoder, fermi, trasferitore e bilici", new[] { ("Elemento", 150.0), ("Posizione [mm]", 90.0), ("Riferimento", 530.0) }, others);
        return t.Finish();
    }

    /// <summary>Nome del punto di arresto: un fermo, il trasferitore o la fine di una sezione inverter.</summary>
    private static string NameAt(LineProject p, List<(string Name, double PosMm)> stops, List<Zone> zones, double mm)
    {
        var s = stops.FirstOrDefault(x => Math.Abs(x.PosMm - mm) < 1);
        if (s.Name != null) return s.Name;
        var z = zones.FirstOrDefault(x => Math.Abs(x.EndMm - mm) < 1);
        return z != null ? "Fine " + z.Label.Split(':')[0].ToLowerInvariant() : Mm(mm);
    }

    /// <summary>Tabelle impaginate su più pagine A4 orizzontali.</summary>
    private sealed class TableWriter
    {
        private const double LineH = 11;
        private readonly string _title;
        private StringBuilder? _c;
        private double _y;
        public List<string> Pages { get; } = new();

        public TableWriter(string title) { _title = title; }

        /// <summary>Chiude l'ultima pagina e restituisce tutte le pagine.</summary>
        public List<string> Finish()
        {
            if (_c != null) { Pages.Add(_c.ToString()); _c = null; }
            return Pages;
        }

        private void NewPage()
        {
            if (_c != null) Pages.Add(_c.ToString());
            _c = new StringBuilder();
            Text(_c, Margin, PageH - 36, 14, _title);
            _y = PageH - 62;
        }

        public void Table(string caption, (string H, double W)[] cols, IEnumerable<string[]> rows)
        {
            var list = rows.ToList();
            if (_c == null || _y < Margin + 5 * LineH) NewPage();
            Header(caption, cols);
            if (list.Count == 0) { Gray(_c!, 0.4); Text(_c!, Margin, _y, 7, "Nessun elemento."); Gray(_c!, 0); _y -= LineH; }
            int r = 0;
            foreach (var row in list)
            {
                if (_y < Margin) { NewPage(); Header(caption + " (segue)", cols); }
                if (r++ % 2 == 1) { Gray(_c!, 0.95); Rect(_c!, Margin, _y - 3, PageW - 2 * Margin, LineH, true); Gray(_c!, 0); }
                double x = Margin + 2;
                for (int k = 0; k < cols.Length && k < row.Length; k++)
                {
                    Text(_c!, x, _y, 6.5, Fit(row[k], cols[k].W));
                    x += cols[k].W;
                }
                _y -= LineH;
            }
            _y -= 14;
        }

        private void Header(string caption, (string H, double W)[] cols)
        {
            Gray(_c!, 0); Text(_c!, Margin, _y, 10, caption); _y -= 14;
            Fill(_c!, (0.88, 0.91, 0.96)); Rect(_c!, Margin, _y - 3, PageW - 2 * Margin, LineH, true); Gray(_c!, 0);
            double x = Margin + 2;
            foreach (var (h, w) in cols) { Text(_c!, x, _y, 6.5, Fit(h, w)); x += w; }
            _y -= LineH;
        }

        private static string Fit(string s, double w)
        {
            int n = (int)(w / 3.2);
            return s.Length <= n ? s : s[..Math.Max(1, n - 1)] + ".";
        }
    }

    // ================================================================== primitive

    private static string F(double v) => StepCyclogramPdf.F(v);
    private static void Text(StringBuilder c, double x, double y, double size, string s) => StepCyclogramPdf.Text(c, x, y, size, s);
    private static void Line(StringBuilder c, double x1, double y1, double x2, double y2) => StepCyclogramPdf.Line(c, x1, y1, x2, y2);
    private static void Fill(StringBuilder c, (double R, double G, double B) k) => c.Append($"{F(k.R)} {F(k.G)} {F(k.B)} rg\n");
    private static void Stroke(StringBuilder c, (double R, double G, double B) k) => c.Append($"{F(k.R)} {F(k.G)} {F(k.B)} RG\n");
    private static void Gray(StringBuilder c, double g) => c.Append($"{F(g)} g {F(g)} G\n");
    private static void Rect(StringBuilder c, double x, double y, double w, double h, bool fill)
        => c.Append($"{F(x)} {F(y)} {F(w)} {F(h)} re {(fill ? "f" : "S")}\n");
    private static (double, double, double) Light((double R, double G, double B) k) => (1 - (1 - k.R) * 0.35, 1 - (1 - k.G) * 0.35, 1 - (1 - k.B) * 0.35);

    private static string M(double mm) => (mm / 1000).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
    private static string Mm(double mm) => Math.Round(mm).ToString("0", CultureInfo.InvariantCulture);
    private static string SignedMm(double mm) => (mm >= 0 ? "+" : "") + Mm(mm) + " mm";
    private static string N2(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture).Replace('.', ',');
}
