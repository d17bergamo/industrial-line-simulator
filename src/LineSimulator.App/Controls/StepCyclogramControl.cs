using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LineSimulator.Engine;

namespace LineSimulator.App.Controls;

/// <summary>Ciclogramma a gradini: livello basso = fermo, medio = velocità bassa/transizione, alto = attivo. Tempi arrotondati a 0,5 s per eccesso.</summary>
public sealed class StepCyclogramControl : FrameworkElement
{
    private const double LabelW = 200, RowH = 26, Header = 24, PxPerSecond = 36;

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<StepRow>), typeof(StepCyclogramControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public IReadOnlyList<StepRow>? Rows { get => (IReadOnlyList<StepRow>?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Rows;
        if (rows == null || rows.Count == 0) return new Size(400, 60);
        return new Size(LabelW + 20 + Math.Max(600, StepCyclogram.TotalS(rows) * PxPerSecond), Header + rows.Count * RowH + 10);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var rows = Rows;
        if (rows == null || rows.Count == 0) { Text(dc, "Nessun dato: avviare la simulazione.", 8, 8, Brushes.Gray, 12); return; }
        double total = Math.Max(0.5, StepCyclogram.TotalS(rows));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA)), 1);
        var axis = new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x3D)), 1.6);
        double tick = total <= 60 ? 0.5 : total <= 300 ? 5 : 30;
        for (double t = 0; t <= total + 1e-9; t += tick)
        {
            double x = LabelW + t * PxPerSecond;
            dc.DrawLine(grid, new Point(x, Header), new Point(x, Header + rows.Count * RowH));
            if (tick >= 5 || Math.Abs(t % 5) < 1e-9) Text(dc, t.ToString("0.#", CultureInfo.InvariantCulture), x - 8, 4, Brushes.DimGray, 10);
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            double yTop = Header + i * RowH, yBase = yTop + RowH - 5, h = RowH - 12;
            Text(dc, r.Label, 6, yTop + 5, Brushes.Black, 11);
            dc.DrawLine(grid, new Point(0, yTop + RowH), new Point(ActualWidth, yTop + RowH));
            double X(double t) => LabelW + t * PxPerSecond;
            double Y(StepLevel l) => yBase - h * (int)l / 2.0;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var pts = r.Points;
                ctx.BeginFigure(new Point(X(pts[0].TimeS), Y(pts[0].Level)), false, false);
                for (int k = 1; k < pts.Count; k++)
                {
                    ctx.LineTo(new Point(X(pts[k].TimeS), Y(pts[k - 1].Level)), true, false);
                    ctx.LineTo(new Point(X(pts[k].TimeS), Y(pts[k].Level)), true, false);
                }
                ctx.LineTo(new Point(X(r.EndS), Y(pts[^1].Level)), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, axis, geo);
        }
    }

    private static void Text(DrawingContext dc, string s, double x, double y, Brush b, double size)
        => dc.DrawText(new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, 1.0), new Point(x, y));
}
