using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.App.Controls;

/// <summary>
/// Pacco in pianta (lunghezza x larghezza) con le legature. La testa è a destra (direzione di marcia).
/// Un clic seleziona una legatura, trascinandola si sposta a passi di 100 mm. Aggiunta e rimozione col pulsanti.
/// </summary>
public sealed class PackagePreviewControl : FrameworkElement
{
    public static readonly DependencyProperty PackageProperty = DependencyProperty.Register(
        nameof(Package), typeof(PackageType), typeof(PackagePreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(PackagePreviewControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(PackagePreviewControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public PackageType? Package { get => (PackageType?)GetValue(PackageProperty); set => SetValue(PackageProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    /// <summary>Indice (nell'elenco ordinato dalla testa) della legatura selezionata, -1 se nessuna.</summary>
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    /// <summary>Sollevato a fine trascinamento: le posizioni sono cambiate.</summary>
    public event Action? StrapsEdited;

    private const double Pad = 30;
    private int _drag = -1;

    private (double X0, double Scale, double Right) Frame()
    {
        double len = Math.Max(1, Package?.LengthMm ?? 1);
        double scale = Math.Max(10, ActualWidth - 2 * Pad) / len;
        return (Pad, scale, Pad + len * scale);
    }

    private double XOf(double posFromHead) { var f = Frame(); return f.Right - posFromHead * f.Scale; }
    private double PosOf(double x) { var f = Frame(); return (f.Right - x) / f.Scale; }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, 150);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var pk = Package;
        if (pk == null) return;
        var f = Frame();
        double len = pk.LengthMm, wid = Math.Max(1, pk.GraphicWidthMm);
        double bodyH = Math.Clamp(wid * f.Scale * 3, 40, 70);      // larghezza ingrandita per leggibilità
        double top = 34;
        var body = new SolidColorBrush(Color.FromRgb(0xD9, 0xC2, 0x9A));
        var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x7A, 0x66, 0x44)), 1);
        var ok = new Pen(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x50)), 4);
        var bad = new Pen(new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), 4);
        var sel = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)), 6);

        Text(dc, "Pacco in pianta (testa a destra) - legature a passi di 100 mm", Pad, 6, 11, Brushes.Gray);
        dc.DrawRectangle(body, edge, new Rect(f.X0, top, len * f.Scale, bodyH));

        var pos = pk.StrapPositionsMm.OrderBy(v => v).ToList();
        for (int i = 0; i < pos.Count; i++)
        {
            double p = pos[i], x = XOf(p);
            bool violates = p < pk.MinStrapDistanceFromEndsMm || p > len - pk.MinStrapDistanceFromEndsMm
                || pos.Where((_, j) => j != i).Any(o => Math.Abs(o - p) < pk.MinDistanceBetweenStrapsMm);
            dc.DrawLine(i == SelectedIndex ? sel : violates ? bad : ok, new Point(x, top - 6), new Point(x, top + bodyH + 6));
            Text(dc, p.ToString("0", CultureInfo.InvariantCulture), x - 14, top + bodyH + 8, 10, violates ? Brushes.Firebrick : Brushes.Black);
        }
        // distanze tra legature e dalle estremità
        var marks = new List<double> { 0 }; marks.AddRange(pos); marks.Add(len);
        double qy = top + bodyH + 30;
        var q = new Pen(Brushes.DimGray, 1);
        for (int i = 1; i < marks.Count; i++)
        {
            double xa = XOf(marks[i - 1]), xb = XOf(marks[i]);
            dc.DrawLine(q, new Point(xa, qy), new Point(xb, qy));
            dc.DrawLine(q, new Point(xa, qy - 3), new Point(xa, qy + 3));
            dc.DrawLine(q, new Point(xb, qy - 3), new Point(xb, qy + 3));
            if (Math.Abs(xa - xb) >= 36)
                Text(dc, (marks[i] - marks[i - 1]).ToString("0", CultureInfo.InvariantCulture), (xa + xb) / 2 - 12, qy + 3, 10, Brushes.DimGray);
        }
        Text(dc, $"{pos.Count} legature - L {pk.LengthMm:0} x larghezza {pk.GraphicWidthMm:0} mm", Pad, ActualHeight - 18, 11, Brushes.Black);
    }

    private static void Text(DrawingContext dc, string s, double x, double y, double size, Brush b)
        => dc.DrawText(new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, 1.0), new Point(x, y));

    private int Hit(Point pt)
    {
        var pk = Package; if (pk == null) return -1;
        var pos = pk.StrapPositionsMm.OrderBy(v => v).ToList();
        int best = -1; double bd = 8;
        for (int i = 0; i < pos.Count; i++)
        {
            double d = Math.Abs(XOf(pos[i]) - pt.X);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var pk = Package; if (pk == null) return;
        pk.StrapPositionsMm = pk.StrapPositionsMm.OrderBy(v => v).ToList();
        _drag = Hit(e.GetPosition(this));
        SelectedIndex = _drag;
        if (_drag >= 0) CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pk = Package; if (pk == null || _drag < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        double p = StrapEditing.Snap(PosOf(e.GetPosition(this).X));
        pk.StrapPositionsMm[_drag] = Math.Clamp(p, StrapEditing.StepMm, Math.Max(StrapEditing.StepMm, pk.LengthMm - StrapEditing.StepMm));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        var pk = Package;
        if (pk != null)
        {
            double? selected = SelectedIndex >= 0 && SelectedIndex < pk.StrapPositionsMm.Count ? pk.StrapPositionsMm[SelectedIndex] : null;
            StrapEditing.SnapAll(pk);
            SelectedIndex = selected is double s ? pk.StrapPositionsMm.IndexOf(s) : -1;
        }
        StrapsEdited?.Invoke();
        InvalidateVisual();
    }
}
