using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LineSimulator.Engine;

namespace LineSimulator.App.Controls;

/// <summary>
/// Ciclogramma: tempo in orizzontale, utenze in verticale. Un clic seleziona l'istante
/// (la vista 3D mostra lo stato di quel momento); la rotella zooma, Maiusc+rotella scorre.
/// </summary>
public sealed class CyclogramControl : FrameworkElement
{
    private const double LabelWidth = 190;
    private const double RowHeight = 20;
    private const double HeaderHeight = 22;

    public static readonly DependencyProperty RecorderProperty = DependencyProperty.Register(
        nameof(Recorder), typeof(SimulationRecorder), typeof(CyclogramControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CursorTimeProperty = DependencyProperty.Register(
        nameof(CursorTime), typeof(double), typeof(CyclogramControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnCursorChanged));

    public static readonly DependencyProperty MaxTimeProperty = DependencyProperty.Register(
        nameof(MaxTime), typeof(double), typeof(CyclogramControl),
        new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(CyclogramControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FollowCursorProperty = DependencyProperty.Register(
        nameof(FollowCursor), typeof(bool), typeof(CyclogramControl), new FrameworkPropertyMetadata(true));

    public SimulationRecorder? Recorder { get => (SimulationRecorder?)GetValue(RecorderProperty); set => SetValue(RecorderProperty, value); }
    public double CursorTime { get => (double)GetValue(CursorTimeProperty); set => SetValue(CursorTimeProperty, value); }
    public double MaxTime { get => (double)GetValue(MaxTimeProperty); set => SetValue(MaxTimeProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public bool FollowCursor { get => (bool)GetValue(FollowCursorProperty); set => SetValue(FollowCursorProperty, value); }

    /// <summary>Ampiezza della finestra temporale visibile (s).</summary>
    private double _window = 120;
    private double _viewStart;

    public CyclogramControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    private static void OnCursorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (CyclogramControl)d;
        if (!c.FollowCursor) return;
        double t = (double)e.NewValue;
        if (t > c._viewStart + c._window * 0.95) c._viewStart = t - c._window * 0.8;
        if (t < c._viewStart) c._viewStart = Math.Max(0, t - c._window * 0.1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Recorder?.Tracks.Count ?? 0;
        double h = HeaderHeight + rows * RowHeight + 4;
        double w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        return new Size(w, h);
    }

    private double TimeToX(double t) => LabelWidth + (t - _viewStart) / _window * Math.Max(1, ActualWidth - LabelWidth - 6);
    private double XToTime(double x) => _viewStart + (x - LabelWidth) / Math.Max(1, ActualWidth - LabelWidth - 6) * _window;

    private static readonly Dictionary<CycleState, Brush> Brushes = new()
    {
        [CycleState.Off] = Make(0xEC, 0xEF, 0xF1),
        [CycleState.On] = Make(0xE7, 0x4C, 0x3C),
        [CycleState.Stopped] = Make(0xD5, 0xD8, 0xDC),
        [CycleState.Accelerating] = Make(0x85, 0xC1, 0xE9),
        [CycleState.Decelerating] = Make(0xF5, 0xB0, 0x41),
        [CycleState.Low] = Make(0x58, 0xD6, 0x8D),
        [CycleState.High] = Make(0x1E, 0x84, 0x49),
        [CycleState.Positioning] = Make(0x8E, 0x44, 0xAD),
        [CycleState.Aligning] = Make(0xF3, 0x9C, 0x12),
        [CycleState.Aligned] = Make(0xF7, 0xDC, 0x6F),
        [CycleState.Idle] = Make(0xEC, 0xEF, 0xF1),
        [CycleState.Preparing] = Make(0xF4, 0xD0, 0x3F),
        [CycleState.Working] = Make(0xE6, 0x7E, 0x22),
        [CycleState.Returning] = Make(0x5D, 0xAD, 0xE2),
        [CycleState.Lifting] = Make(0xA9, 0xCC, 0xE3),
        [CycleState.Translating] = Make(0x29, 0x80, 0xB9),
        [CycleState.Lowering] = Make(0x7F, 0xB3, 0xD5),
        [CycleState.Depositing] = Make(0x1A, 0x52, 0x76),
        [CycleState.Consent] = Make(0xAB, 0xEB, 0xC6),
        [CycleState.Interlock] = Make(0xF1, 0x94, 0x8A),
        [CycleState.Waiting] = Make(0xD6, 0xEA, 0xF8),
        [CycleState.InPosition] = Make(0xBB, 0x8F, 0xCE),
        [CycleState.WaitConsent] = Make(0xFA, 0xD7, 0xA0),
        [CycleState.Completed] = Make(0x82, 0xE0, 0xAA),
        [CycleState.Unloading] = Make(0x48, 0xC9, 0xB0),
        [CycleState.Fault] = Make(0xC0, 0x39, 0x2B),
        [CycleState.Closing] = Make(0xAF, 0x60, 0x1A),
        [CycleState.Closed] = Make(0x78, 0x42, 0x12),
        [CycleState.Opening] = Make(0xE5, 0x98, 0x66),
        [CycleState.Ejecting] = Make(0x94, 0x31, 0x26),
        [CycleState.Stabilizing] = Make(0x76, 0xD7, 0xC4),
        [CycleState.Acquiring] = Make(0x11, 0x78, 0x64),
        [CycleState.Raised] = Make(0xE7, 0x4C, 0x3C)
    };

    private static Brush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public static string StateLabel(CycleState s) => s switch
    {
        CycleState.Off => "OFF", CycleState.On => "ON", CycleState.Stopped => "Fermo",
        CycleState.Accelerating => "Accelerazione", CycleState.Decelerating => "Frenatura",
        CycleState.Low => "Velocità bassa", CycleState.High => "Velocità alta", CycleState.Positioning => "Posizionamento",
        CycleState.Aligning => "Allineamento", CycleState.Aligned => "Attesa (allineato)", CycleState.Idle => "Attesa/Libero",
        CycleState.Preparing => "Preparazione", CycleState.Working => "Lavorazione", CycleState.Returning => "Ritorno",
        CycleState.Lifting => "Sollevamento", CycleState.Translating => "Traslazione", CycleState.Lowering => "Abbassamento",
        CycleState.Depositing => "Deposito", CycleState.Consent => "Consenso", CycleState.Interlock => "Interblocco",
        CycleState.Waiting => "Attesa", CycleState.InPosition => "In posizione", CycleState.WaitConsent => "Attesa consenso",
        CycleState.Completed => "Completato", CycleState.Unloading => "Scarico", CycleState.Fault => "Guasto/allarme",
        CycleState.Closing => "Chiusura", CycleState.Closed => "Chiuso", CycleState.Opening => "Apertura",
        CycleState.Ejecting => "Espulsione", CycleState.Stabilizing => "Stabilizzazione", CycleState.Acquiring => "Acquisizione peso",
        CycleState.Raised => "Sollevato",
        _ => s.ToString()
    };

    public static IEnumerable<(string Label, Brush Brush)> Legend =>
        Brushes.Where(kv => kv.Key != CycleState.Idle).Select(kv => (StateLabel(kv.Key), kv.Value));

    protected override void OnRender(DrawingContext dc)
    {
        var bg = System.Windows.Media.Brushes.White;
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var rec = Recorder;
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0xE8, 0xEB)), 1);
        var textBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x3D));

        // Asse dei tempi
        double step = NiceStep(_window / 10);
        double tFirst = Math.Ceiling(_viewStart / step) * step;
        for (double t = tFirst; t <= _viewStart + _window; t += step)
        {
            double x = TimeToX(t);
            dc.DrawLine(gridPen, new Point(x, HeaderHeight - 4), new Point(x, ActualHeight));
            var ft = new FormattedText(t.ToString("0.#", CultureInfo.CurrentCulture) + " s", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 10, textBrush, dpi);
            dc.DrawText(ft, new Point(x + 2, 3));
        }
        if (rec == null) return;

        double y = HeaderHeight;
        TrackGroup? lastGroup = null;
        foreach (var track in rec.Tracks)
        {
            if (lastGroup != track.Group)
            {
                dc.DrawLine(new Pen(System.Windows.Media.Brushes.Gray, 0.5), new Point(0, y), new Point(ActualWidth, y));
                lastGroup = track.Group;
            }
            var label = new FormattedText(track.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, textBrush, dpi)
            {
                MaxTextWidth = LabelWidth - 8,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            dc.DrawText(label, new Point(4, y + 3));

            dc.PushClip(new RectangleGeometry(new Rect(LabelWidth, y, Math.Max(0, ActualWidth - LabelWidth), RowHeight)));
            foreach (var seg in track.Segments)
            {
                if (seg.EndS < _viewStart || seg.StartS > _viewStart + _window) continue;
                double x0 = TimeToX(seg.StartS), x1 = Math.Max(x0 + 0.5, TimeToX(seg.EndS));
                double inset = seg.State is CycleState.Off or CycleState.Idle or CycleState.Stopped ? 7 : 3;
                dc.DrawRectangle(Brushes.TryGetValue(seg.State, out var b) ? b : System.Windows.Media.Brushes.Gray, null,
                    new Rect(x0, y + inset, x1 - x0, RowHeight - 2 * inset));
            }
            dc.Pop();
            y += RowHeight;
        }

        // Cursore
        double cx = TimeToX(CursorTime);
        if (cx >= LabelWidth)
        {
            dc.DrawLine(new Pen(System.Windows.Media.Brushes.Red, 1.5), new Point(cx, 0), new Point(cx, ActualHeight));
            var ft = new FormattedText(CursorTime.ToString("0.00", CultureInfo.CurrentCulture) + " s", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 10, System.Windows.Media.Brushes.Red, dpi);
            dc.DrawText(ft, new Point(cx + 3, HeaderHeight - 12));
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        if (p.X < LabelWidth) return;
        double t = Math.Clamp(XToTime(p.X), 0, Math.Max(0, MaxTime));
        FollowCursor = false;
        CursorTime = t;
        TimeSelected?.Invoke(this, t);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var rec = Recorder;
        if (rec == null) return;
        var p = e.GetPosition(this);
        int row = (int)((p.Y - HeaderHeight) / RowHeight);
        if (p.X < LabelWidth || row < 0 || row >= rec.Tracks.Count) { ToolTip = null; return; }
        double t = XToTime(p.X);
        var seg = rec.Tracks[row].At(t);
        ToolTip = seg == null ? null :
            $"{rec.Tracks[row].Label}\n{StateLabel(seg.State)}  {seg.StartS:0.00} → {seg.EndS:0.00} s ({seg.DurationS:0.00} s)" +
            (string.IsNullOrEmpty(seg.Detail) ? "" : "\n" + seg.Detail);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _viewStart = Math.Max(0, _viewStart - Math.Sign(e.Delta) * _window * 0.1);
        }
        else
        {
            double anchor = p.X >= LabelWidth ? XToTime(p.X) : _viewStart;
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double newWindow = Math.Clamp(_window * factor, 1, Math.Max(10, MaxTime * 1.2));
            _viewStart = Math.Max(0, anchor - (anchor - _viewStart) * newWindow / _window);
            _window = newWindow;
        }
        FollowCursor = false;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Mostra tutta la durata registrata.</summary>
    public void FitAll()
    {
        _viewStart = 0;
        _window = Math.Max(10, MaxTime);
        InvalidateVisual();
    }

    public event EventHandler<double>? TimeSelected;
}
