using System.Globalization;
using System.Text;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;

namespace LineSimulator.Infrastructure;

/// <summary>
/// PDF del ciclogramma: Gantt dell'avanzamento dei pacchi sulla linea (un tratto per movimento o sosta, con distanza,
/// velocità e accelerazione), tabella dei tratti per pacco e, in coda, il ciclogramma a gradini delle utenze.
/// A4 orizzontale, scritto direttamente come lo è il ciclogramma a gradini.
/// </summary>
public static class PackageGanttPdf
{
    private const double PageW = 842, PageH = 595, Margin = 36, LabelW = 150, RowH = 26, Top = 92, BarH = 13;

    /// <summary>Salva il PDF unico: Gantt dei pacchi, dettaglio dei tratti, ciclogramma a gradini (se presente).</summary>
    public static void Save(string path, IReadOnlyList<GanttRow> gantt, IReadOnlyList<StepRow>? stepRows, string title)
        => File.WriteAllBytes(path, Build(gantt, stepRows, title));

    public static byte[] Build(IReadOnlyList<GanttRow> gantt, IReadOnlyList<StepRow>? stepRows, string title)
    {
        var pages = new List<string>();
        pages.AddRange(GanttPages(gantt, title));
        pages.AddRange(DetailPages(gantt, title));
        if (stepRows != null && stepRows.Count > 0) pages.AddRange(StepCyclogramPdf.Pages(stepRows, title));
        return StepCyclogramPdf.Assemble(pages);
    }

    // ------------------------------------------------------------------ Colori delle fasi

    private static (double R, double G, double B) Color(GanttSegment s) => s.State switch
    {
        PackageState.Strapping => (0.93, 0.55, 0.13),
        PackageState.OnTransfer => (0.30, 0.68, 0.35),
        PackageState.Aligning or PackageState.Aligned when !s.Moving => (0.62, 0.45, 0.80),
        PackageState.Positioning when s.Moving => (0.45, 0.66, 0.95),
        _ when s.Moving => (0.18, 0.42, 0.78),
        _ => (0.74, 0.74, 0.74)
    };

    private static readonly (string Label, (double, double, double) Rgb)[] Legend =
    {
        ("Trasporto", (0.18, 0.42, 0.78)), ("Posizionamento", (0.45, 0.66, 0.95)), ("Legatura", (0.93, 0.55, 0.13)),
        ("Intestatura / attesa trasferitore", (0.62, 0.45, 0.80)), ("Sul trasferitore", (0.30, 0.68, 0.35)), ("Fermo / attesa", (0.74, 0.74, 0.74))
    };

    // ------------------------------------------------------------------ Pagine del Gantt

    private static IEnumerable<string> GanttPages(IReadOnlyList<GanttRow> rows, string title)
    {
        double total = Math.Max(1, rows.Count > 0 ? rows.Max(r => r.EndS) : 1);
        double plotW = PageW - 2 * Margin - LabelW;
        double tick = total <= 60 ? 5 : total <= 300 ? 20 : total <= 900 ? 60 : 120;
        int perPage = Math.Max(1, (int)((PageH - Top - Margin) / RowH));
        int pageCount = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)perPage));
        for (int pg = 0; pg < pageCount; pg++)
        {
            var c = new StringBuilder();
            StepCyclogramPdf.Text(c, Margin, PageH - 36, 14, $"{title} - Gantt dell'avanzamento dei pacchi");
            StepCyclogramPdf.Text(c, Margin, PageH - 50, 8,
                $"Una riga per pacco; nei tratti di movimento: distanza percorsa, velocità massima e accelerazione/decelerazione massime. Durata: {N(total, "0.0")} s. Pagina {pg + 1}/{pageCount}");
            double lx = Margin;
            foreach (var (label, rgb) in Legend)
            {
                Fill(c, rgb); Rect(c, lx, PageH - 66, 10, 7, fill: true);
                StepCyclogramPdf.Text(c, lx + 13, PageH - 65, 7, label);
                lx += 18 + label.Length * 3.6;
            }
            c.Append("0 g\n");
            double x0 = Margin + LabelW, yTop = PageH - Top;
            var slice = rows.Skip(pg * perPage).Take(perPage).ToList();
            c.Append("0.85 G 0.3 w\n");
            for (double t = 0; t <= total + 1e-9; t += tick)
            {
                double x = x0 + plotW * t / total;
                StepCyclogramPdf.Line(c, x, yTop + 6, x, yTop - slice.Count * RowH);
                StepCyclogramPdf.Text(c, x - 6, yTop + 9, 6, N(t, "0"));
            }
            StepCyclogramPdf.Text(c, x0 + plotW - 10, yTop + 17, 6, "t [s]");
            for (int i = 0; i < slice.Count; i++)
            {
                var r = slice[i];
                double yBar = yTop - (i + 1) * RowH + 7;
                c.Append("0 g\n");
                StepCyclogramPdf.Text(c, Margin, yBar + 7, 7.5, $"{r.PackageId}  {r.TypeId}");
                StepCyclogramPdf.Text(c, Margin, yBar - 2, 6, $"{N(r.LengthM, "0.0")} m · {N(r.TotalDistanceM, "0.0")} m percorsi · v max {N(r.MaxSpeed, "0.00")} m/s");
                c.Append("0.9 G 0.3 w\n");
                StepCyclogramPdf.Line(c, Margin, yBar - 5, x0 + plotW, yBar - 5);
                foreach (var s in r.Segments)
                {
                    double xa = x0 + plotW * s.StartS / total, xb = x0 + plotW * s.EndS / total, w = Math.Max(0.4, xb - xa);
                    Fill(c, Color(s)); Rect(c, xa, yBar, w, BarH, fill: true);
                    if (!s.Moving) continue;
                    c.Append("1 g\n");
                    string text = w >= 105
                        ? $"{N(s.DistanceM, "0.0")} m · {N(s.MaxSpeed, "0.00")} m/s · +{N(s.MaxAcceleration, "0.0")}/-{N(s.MaxDeceleration, "0.0")} m/s²"
                        : w >= 50 ? $"{N(s.DistanceM, "0.0")} m · {N(s.MaxSpeed, "0.00")} m/s"
                        : w >= 18 ? $"{N(s.DistanceM, "0.0")} m" : "";
                    if (text.Length > 0) StepCyclogramPdf.Text(c, xa + 2, yBar + 4, 5.5, text);
                }
            }
            c.Append("0 g\n");
            yield return c.ToString();
        }
    }

    // ------------------------------------------------------------------ Tabella dei tratti

    private static readonly (string H, double W)[] Cols =
    {
        ("Inizio [s]", 50), ("Fine [s]", 50), ("Durata [s]", 52), ("Fase", 140), ("Testa da [mm]", 66), ("Testa a [mm]", 66),
        ("Distanza [m]", 60), ("V max [m/s]", 58), ("V media [m/s]", 62), ("Acc. max [m/s²]", 70), ("Dec. max [m/s²]", 70)
    };

    private static IEnumerable<string> DetailPages(IReadOnlyList<GanttRow> rows, string title)
    {
        const double lineH = 10.5;
        var lines = new List<(bool Header, string[] Cells, string? Caption)>();
        foreach (var r in rows)
        {
            lines.Add((true, Array.Empty<string>(),
                $"{r.PackageId} {r.TypeId} ({N(r.LengthM, "0.0")} m): {N(r.TotalDistanceM, "0.0")} m percorsi in {N(r.EndS - r.StartS, "0.0")} s, v max {N(r.MaxSpeed, "0.00")} m/s, acc. max {N(r.MaxAcceleration, "0.0")} m/s², dec. max {N(r.MaxDeceleration, "0.0")} m/s²"));
            foreach (var s in r.Segments)
                lines.Add((false, new[]
                {
                    N(s.StartS, "0.0"), N(s.EndS, "0.0"), N(s.DurationS, "0.0"), s.Label,
                    N(s.FromM * 1000, "0"), N(s.ToM * 1000, "0"),
                    s.Moving ? N(s.DistanceM, "0.00") : "-", s.Moving ? N(s.MaxSpeed, "0.00") : "-", s.Moving ? N(s.MeanSpeed, "0.00") : "-",
                    s.Moving ? N(s.MaxAcceleration, "0.00") : "-", s.Moving ? N(s.MaxDeceleration, "0.00") : "-"
                }, null));
        }
        int perPage = (int)((PageH - 80 - Margin) / lineH);
        int pageCount = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)perPage));
        for (int pg = 0; pg < pageCount; pg++)
        {
            var c = new StringBuilder();
            StepCyclogramPdf.Text(c, Margin, PageH - 36, 14, $"{title} - tratti dei pacchi");
            StepCyclogramPdf.Text(c, Margin, PageH - 50, 8, $"Movimenti e soste di ogni pacco: posizione della testa dall'inizio della linea, distanza, velocità e accelerazioni. Pagina {pg + 1}/{pageCount}");
            double y = PageH - 70, x = Margin;
            foreach (var (h, w) in Cols) { StepCyclogramPdf.Text(c, x, y, 6.5, h); x += w; }
            c.Append("0.6 G 0.4 w\n"); StepCyclogramPdf.Line(c, Margin, y - 3, PageW - Margin, y - 3);
            y -= lineH + 2;
            foreach (var (header, cells, caption) in lines.Skip(pg * perPage).Take(perPage))
            {
                if (header)
                {
                    c.Append("0.95 g\n"); Rect(c, Margin, y - 2.5, PageW - 2 * Margin, lineH, fill: true); c.Append("0 g\n");
                    StepCyclogramPdf.Text(c, Margin + 2, y, 7, caption!);
                }
                else
                {
                    x = Margin;
                    for (int k = 0; k < cells.Length; k++) { StepCyclogramPdf.Text(c, x, y, 6.5, cells[k]); x += Cols[k].W; }
                }
                y -= lineH;
            }
            yield return c.ToString();
        }
    }

    // ------------------------------------------------------------------ Primitive

    private static string N(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture).Replace('.', ',');
    private static void Fill(StringBuilder c, (double R, double G, double B) rgb)
        => c.Append(StepCyclogramPdf.F(rgb.R)).Append(' ').Append(StepCyclogramPdf.F(rgb.G)).Append(' ').Append(StepCyclogramPdf.F(rgb.B)).Append(" rg\n");
    private static void Rect(StringBuilder c, double x, double y, double w, double h, bool fill)
        => c.Append(StepCyclogramPdf.F(x)).Append(' ').Append(StepCyclogramPdf.F(y)).Append(' ').Append(StepCyclogramPdf.F(w)).Append(' ').Append(StepCyclogramPdf.F(h)).Append(fill ? " re f\n" : " re S\n");
}
