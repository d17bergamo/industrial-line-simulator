using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LineSimulator.Models;

namespace LineSimulator.App.Controls;

/// <summary>
/// Anteprima del pacco in vista laterale (lunghezza x altezza) e dall'alto (lunghezza x larghezza) con le legature
/// posizionate a mano: trascina un segno per spostarlo, doppio clic per aggiungerne uno, clic destro per toglierlo.
/// Le posizioni sono misurate dalla testa del pacco (a destra, direzione di marcia).
/// </summary>
public sealed class PackagePreviewControl : FrameworkElement
{
    public static readonly DependencyProperty PackageProperty = DependencyProperty.Register(
        nameof(Package), typeof(PackageType), typeof(PackagePreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(PackagePreviewControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public PackageType? Package { get => (PackageType?)GetValue(PackageProperty); set => SetValue(PackageProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    /// <summary>Sollevato quando le posizioni delle legature sono state modificate col mouse.</summary>
    public event Action? StrapsEdited;

    private const double Pad = 24;
    private int _drag = -1;

    private (double X0, double Scale, double Right) Frame()
    {
        double len = Math.Max(1, Package?.LengthMm ?? 1);
        double usable = Math.Max(10, ActualWidth - 2 * Pad);
        double scale = usable / len;
        return (Pad, scale, Pad + len * scale);
    }

    private double XOf(double posFromHead) { var f = Frame(); return f.Right - posFromHead * f.Scale; }
    private double PosOf(double x) { var f = Frame(); return (f.Right - x) / f.Scale; }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, 230);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var pk = Package;
        if (pk == null) return;
        var f = Frame();
        double len = pk.LengthMm, hgt = Math.Max(1, pk.GraphicHeightMm), wid = Math.Max(1, pk.GraphicWidthMm);
        double hs = Math.Min(f.Scale, 70.0 / hgt), ws = Math.Min(f.Scale, 60.0 / wid);
        double sideH = hgt * hs, topH = wid * ws;
        double ySide = 34, yTop = ySide + sideH + 44;
        var body = new SolidColorBrush(Color.FromRgb(0xD9, 0xC2, 0x9A));
        var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x7A, 0x66, 0x44)), 1);
        var ok = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xB2)), 3);
        var bad = new Pen(new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), 3);

        Text(dc, "Vista laterale (testa a destra)", Pad, 6, 11, Brushes.Gray);
        dc.DrawRectangle(body, edge, new Rect(f.X0, ySide, len * f.Scale, sideH));
        Text(dc, "Vista dall'alto", Pad, yTop - 18, 11, Brushes.Gray);
        dc.DrawRectangle(body, edge, new Rect(f.X0, yTop, len * f.Scale, topH));

        var sorted = pk.StrapPositionsMm.Select((p, i) => (p, i)).ToList();
        for (int k = 0; k < sorted.Count; k++)
        {
            double p = sorted[k].p, x = XOf(p);
            bool violates = p < pk.MinStrapDistanceFromEndsMm || p > len - pk.MinStrapDistanceFromEndsMm
                || sorted.Any(o => o.i != sorted[k].i && Math.Abs(o.p - p) < pk.MinDistanceBetweenStrapsMm);
            var pen = violates ? bad : ok;
            dc.DrawLine(pen, new Point(x, ySide - 4), new Point(x, ySide + sideH + 4));
            dc.DrawLine(pen, new Point(x, yTop - 4), new Point(x, yTop + topH + 4));
            Text(dc, p.ToString("0", CultureInfo.InvariantCulture), x - 12, yTop + topH + 6, 10, violates ? Brushes.Firebrick : Brushes.Black);
        }
        // quote tra legature e dalle estremità (lungo la vista laterale)
        var pos = pk.StrapPositionsMm.OrderBy(v => v).ToList();
        var marks = new List<double> { 0 }; marks.AddRange(pos); marks.Add(len);
        for (int i = 1; i < marks.Count; i++)
        {
            double xa = XOf(marks[i - 1]), xb = XOf(marks[i]);
            double d = marks[i] - marks[i - 1];
            if (Math.Abs(xa - xb) < 34) continue;
            Text(dc, d.ToString("0", CultureInfo.InvariantCulture), (xa + xb) / 2 - 12, ySide + sideH + 10, 10, Brushes.DimGray);
        }
        Text(dc, $"{pk.StrapPositionsMm.Count} legature - L {pk.LengthMm:0} x larghezza {pk.GraphicWidthMm:0} x altezza {pk.GraphicHeightMm:0} mm",
            Pad, ActualHeight - 18, 11, Brushes.Black);
    }

    private static void Text(DrawingContext dc, string s, double x, double y, double size, Brush b)
        => dc.DrawText(new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, 1.0), new Point(x, y));

    private int Hit(Point pt)
    {
        var pk = Package; if (pk == null) return -1;
        int best = -1; double bd = 7;
        for (int i = 0; i < pk.StrapPositionsMm.Count; i++)
        {
            double d = Math.Abs(XOf(pk.StrapPositionsMm[i]) - pt.X);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var pk = Package; if (pk == null) return;
        var pt = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            double p = Math.Round(PosOf(pt.X) / 10) * 10;
            if (p > 0 && p < pk.LengthMm) { pk.StrapPositionsMm.Add(p); pk.StrapPositionsMm.Sort(); StrapsEdited?.Invoke(); InvalidateVisual(); }
            return;
        }
        _drag = Hit(pt);
        if (_drag >= 0) CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pk = Package; if (pk == null || _drag < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        double p = Math.Round(PosOf(e.GetPosition(this).X) / 10) * 10;
        pk.StrapPositionsMm[_drag] = Math.Clamp(p, 0, pk.LengthMm);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        Package?.StrapPositionsMm.Sort();
        StrapsEdited?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var pk = Package; if (pk == null) return;
        int i = Hit(e.GetPosition(this));
        if (i < 0) return;
        pk.StrapPositionsMm.RemoveAt(i);
        StrapsEdited?.Invoke();
        InvalidateVisual();
    }
}
