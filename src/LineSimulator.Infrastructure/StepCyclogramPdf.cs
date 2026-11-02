using System.Globalization;
using System.Text;
using LineSimulator.Engine;

namespace LineSimulator.Infrastructure;

/// <summary>Esporta il ciclogramma a gradini in PDF (A4 orizzontale, scritto direttamente, senza librerie esterne).</summary>
public static class StepCyclogramPdf
{
    private const double PageW = 842, PageH = 595, Margin = 36, LabelW = 190, RowH = 22, Top = 70;

    public static void Save(string path, IReadOnlyList<StepRow> rows, string title)
        => File.WriteAllBytes(path, Build(rows, title));

    public static byte[] Build(IReadOnlyList<StepRow> rows, string title)
    {
        double total = Math.Max(StepCyclogram.RoundingS, StepCyclogram.TotalS(rows));
        int perPage = Math.Max(1, (int)((PageH - Top - Margin - 20) / RowH));
        var pages = new List<string>();
        int pageCount = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)perPage));
        double plotW = PageW - 2 * Margin - LabelW;
        double tick = total <= 30 ? 0.5 : total <= 120 ? 5 : total <= 600 ? 10 : 60;
        for (int pg = 0; pg < pageCount; pg++)
        {
            var c = new StringBuilder();
            Text(c, Margin, PageH - 36, 14, $"{title} - ciclogramma a gradini");
            Text(c, Margin, PageH - 52, 8, $"Tempi arrotondati a {F(StepCyclogram.RoundingS)} s per eccesso (solo in questo grafico). Durata: {F(total)} s. Pagina {pg + 1}/{pageCount}");
            var slice = rows.Skip(pg * perPage).Take(perPage).ToList();
            double x0 = Margin + LabelW, yTop = PageH - Top;
            // griglia verticale
            c.Append("0.85 G 0.3 w\n");
            for (double t = 0; t <= total + 1e-9; t += tick)
            {
                double x = x0 + plotW * t / total;
                Line(c, x, yTop + 6, x, yTop - slice.Count * RowH);
                if (tick >= 0.5 && (tick >= 5 || Math.Abs(t % 5) < 1e-9)) Text(c, x - 6, yTop + 10, 6, F(t));
            }
            c.Append("0 G 0.8 w\n");
            for (int i = 0; i < slice.Count; i++)
            {
                var r = slice[i];
                double yBase = yTop - (i + 1) * RowH + 4, h = RowH - 9;
                Text(c, Margin, yBase + 2, 7, Trim(r.Label, 46));
                c.Append("0.9 G 0.3 w\n"); Line(c, Margin, yBase - 4, x0 + plotW, yBase - 4); c.Append("0 G 0.8 w\n");
                double X(double t) => x0 + plotW * Math.Min(t, total) / total;
                double Y(StepLevel l) => yBase + h * (int)l / 2.0;
                var pts = r.Points;
                var path = new StringBuilder();
                path.Append(F(X(pts[0].TimeS))).Append(' ').Append(F(Y(pts[0].Level))).Append(" m\n");
                for (int k = 1; k < pts.Count; k++)
                {
                    path.Append(F(X(pts[k].TimeS))).Append(' ').Append(F(Y(pts[k - 1].Level))).Append(" l\n");
                    path.Append(F(X(pts[k].TimeS))).Append(' ').Append(F(Y(pts[k].Level))).Append(" l\n");
                }
                path.Append(F(X(r.EndS))).Append(' ').Append(F(Y(pts[^1].Level))).Append(" l S\n");
                c.Append(path);
            }
            pages.Add(c.ToString());
        }
        return Assemble(pages);
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + ".";
    private static void Line(StringBuilder c, double x1, double y1, double x2, double y2)
        => c.Append(F(x1)).Append(' ').Append(F(y1)).Append(" m ").Append(F(x2)).Append(' ').Append(F(y2)).Append(" l S\n");

    private static void Text(StringBuilder c, double x, double y, double size, string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch == '(' || ch == ')' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 32) sb.Append(' ');
            else if (ch < 256) sb.Append(ch is 'à' ? "\\340" : ch is 'è' ? "\\350" : ch is 'é' ? "\\351" : ch is 'ì' ? "\\354" : ch is 'ò' ? "\\362" : ch is 'ù' ? "\\371" : ch.ToString());
            else sb.Append('?');
        }
        c.Append("BT /F1 ").Append(F(size)).Append(" Tf ").Append(F(x)).Append(' ').Append(F(y)).Append(" Td (").Append(sb).Append(") Tj ET\n");
    }

    private static byte[] Assemble(List<string> pages)
    {
        var objs = new List<string>();
        // 1 catalogo, 2 pagine, 3 font, poi per ogni pagina: oggetto pagina + contenuto
        objs.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{4 + i * 2} 0 R"));
        objs.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (int i = 0; i < pages.Count; i++)
        {
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageW)} {F(PageH)}] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            objs.Add($"<< /Length {Encoding.Latin1.GetByteCount(pages[i])} >>\nstream\n{pages[i]}endstream");
        }
        var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        W($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
