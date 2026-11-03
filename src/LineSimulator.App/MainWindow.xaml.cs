using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LineSimulator.App.Controls;
using LineSimulator.App.Mvvm;
using LineSimulator.App.ViewModels;
using LineSimulator.Models;

namespace LineSimulator.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Rendering.PackageScene _packageScene = new();
    private PackageType? _lastPackage3D;

    /// <summary>Ridisegna il pacco 3D; l'inquadratura si adatta solo quando cambia il pacco selezionato.</summary>
    private void RefreshPackage3D()
    {
        var sel = _vm.SelectedPackageType;
        _packageScene.Build(sel);
        if (sel == null || ReferenceEquals(sel, _lastPackage3D)) return;
        _lastPackage3D = sel;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var cam = PackageViewport.Camera;
            var b = _packageScene.Bounds;
            if (cam == null || b.IsEmpty) return;
            var c = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            double d = Math.Max(b.SizeX, 3) * 0.9;
            var look = new Vector3D(0.35, 1, -0.55); look.Normalize();
            cam.Position = c - look * d; cam.LookDirection = look * d; cam.UpDirection = new Vector3D(0, 0, 1);
            PackageViewport.ZoomExtents(200);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Vista 3D della sezione aperta (layout o simulazioni).</summary>
    private HelixToolkit.Wpf.HelixViewport3D ActiveViewport => _vm.CurrentSection == AppSection.Layout ? LayoutViewport : Viewport;
    private Rendering.LineScene ActiveScene => _vm.CurrentSection == AppSection.Layout ? _vm.LayoutScene : _vm.Scene;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        Viewport.Children.Add(_vm.Scene.Visual);
        LayoutViewport.Children.Add(_vm.LayoutScene.Visual);
        _vm.SceneRebuilt += () => Dispatcher.BeginInvoke(new Action(() => SetView(new Vector3D(0.5, 1, -0.8), new Vector3D(0, 0, 1))));
        _vm.SectionOpened += _ => Dispatcher.BeginInvoke(new Action(() => SetView(new Vector3D(0.5, 1, -0.8), new Vector3D(0, 0, 1))),
            System.Windows.Threading.DispatcherPriority.Loaded);
        LegendList.ItemsSource = CyclogramControl.Legend.Select(x => new { x.Label, x.Brush }).ToList();

        InputBindings.Add(new KeyBinding(_vm.PlayCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(_vm.StepCommand, Key.F10, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(_vm.NextEventCommand, Key.F11, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(_vm.SaveCommand, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.OpenCommand, Key.O, ModifierKeys.Control));
        Loaded += (_, _) => SetView(new Vector3D(0.5, 1, -0.8), new Vector3D(0, 0, 1));
        Closing += OnClosing;
        PackagePreview.StrapsEdited += _vm.OnStrapsEditedInPreview;
        PackageViewport.Children.Add(_packageScene.Visual);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.PackagePreviewVersion) or nameof(MainViewModel.SelectedPackageType)) RefreshPackage3D();
        };
        RefreshPackage3D();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.CanClose()) e.Cancel = true;
    }

    private void LineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeViewModel node) _vm.SelectedNode = node;
    }

    /// <summary>Selezione diretta di un componente nel modello 3D.</summary>
    private void Viewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => PickAt(Viewport, _vm.Scene, e);
    private object? PickAt(HelixToolkit.Wpf.HelixViewport3D viewport, Rendering.LineScene scene, MouseButtonEventArgs e)
    {
        var vp = viewport.Viewport;
        var pt = e.GetPosition(vp);
        if (VisualTreeHelper.HitTest(vp, pt) is RayMeshGeometry3DHitTestResult hit && hit.ModelHit != null)
        {
            var obj = scene.Pick(hit.ModelHit);
            if (obj != null) { _vm.SelectModel(obj); return obj; }
        }
        return null;
    }

    // ------------------------------------------------------------ Imposta layout: selezione e spostamento col mouse

    private bool _moving;
    private double _lastXm;

    /// <summary>Punto del piano dei rulli sotto il cursore (coordinate di scena, metri).</summary>
    private Point3D? PlanePoint(Point pt, double z = 0)
        => HelixToolkit.Wpf.Viewport3DHelper.UnProject(LayoutViewport.Viewport, pt, new Point3D(0, 0, z), new Vector3D(0, 0, 1));

    private void LayoutViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var obj = PickAt(LayoutViewport, _vm.LayoutScene, e);
        if (obj == null || !MainViewModel.CanMove(obj)) return;
        var p = PlanePoint(e.GetPosition(LayoutViewport.Viewport));
        if (p == null) return;
        _moving = true;
        _vm.LayoutScene.Dragging = true;
        _lastXm = p.Value.X;
        LayoutViewport.CaptureMouse();
    }

    private void LayoutViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_moving || e.LeftButton != MouseButtonState.Pressed) return;
        var p = PlanePoint(e.GetPosition(LayoutViewport.Viewport));
        if (p == null) return;
        double dx = (p.Value.X - _lastXm) * 1000.0;
        if (Math.Abs(dx) < 1) return;
        _lastXm = p.Value.X;
        _vm.MoveSelectedBy(dx, p.Value.Y);
    }

    private void LayoutViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_moving) return;
        _moving = false;
        _vm.LayoutScene.Dragging = false;
        LayoutViewport.ReleaseMouseCapture();
        _vm.CommitMove();
    }

    // ------------------------------------------------------------ Imposta layout: libreria e drag & drop

    private Point _dragStart;
    private LibraryEntry? _dragEntry;

    private void Library_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragEntry = (e.OriginalSource as FrameworkElement)?.DataContext as LibraryEntry;
    }

    private void Library_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragEntry == null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var entry = _dragEntry;
        _dragEntry = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(LibraryEntry), entry), DragDropEffects.Copy);
        _vm.LayoutScene.HideGhost();
    }

    private void LayoutViewport_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LibraryEntry))) { e.Effects = DragDropEffects.None; return; }
        var entry = (LibraryEntry)e.Data.GetData(typeof(LibraryEntry))!;
        var p = PlanePoint(e.GetPosition(LayoutViewport.Viewport));
        if (p == null) { e.Effects = DragDropEffects.None; _vm.LayoutScene.HideGhost(); e.Handled = true; return; }
        e.Effects = DragDropEffects.Copy;
        // la linea parte da X = 0: l'anteprima non esce dal piano di progettazione
        double x = Math.Round(p.Value.X * 100) / 100, y = Math.Round(p.Value.Y * 100) / 100;
        _vm.LayoutScene.ShowGhost(x, y, entry.LengthM, entry.WidthM, entry.HeightM);
        e.Handled = true;
    }

    private void LayoutViewport_DragLeave(object sender, DragEventArgs e) => _vm.LayoutScene.HideGhost();

    private void LayoutViewport_Drop(object sender, DragEventArgs e)
    {
        _vm.LayoutScene.HideGhost();
        if (!e.Data.GetDataPresent(typeof(LibraryEntry))) return;
        var entry = (LibraryEntry)e.Data.GetData(typeof(LibraryEntry))!;
        var p = PlanePoint(e.GetPosition(LayoutViewport.Viewport));
        if (p == null) return;
        _vm.PlaceFromLibrary(entry, p.Value.X * 1000.0, p.Value.Y);
        e.Handled = true;
    }

    private void ToggleLibrary_Click(object sender, RoutedEventArgs e)
    {
        bool show = LibraryColumn.Width.Value < 1;
        LibraryColumn.Width = new GridLength(show ? 290 : 0);
        LibrarySplitterColumn.Width = new GridLength(show ? 5 : 0);
        LibraryPanel.Visibility = LibrarySplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetView(Vector3D direction, Vector3D up)
    {
        var b = ActiveScene.Bounds;
        if (b.IsEmpty) return;
        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        double dist = Math.Max(Math.Max(b.SizeX, b.SizeY), 10) * 1.1;
        direction.Normalize();
        var look = direction * dist;
        var viewport = ActiveViewport;
        var cam = viewport.Camera;
        if (cam == null) return;
        cam.Position = center - look;
        cam.LookDirection = look;
        cam.UpDirection = up;
        viewport.ZoomExtents(300);
    }

    private void ViewTop_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(0, 0, -1), new Vector3D(0, 1, 0));
    private void ViewSide_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(1, 0, 0), new Vector3D(0, 0, 1));
    private void ViewFront_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(0, 1, 0), new Vector3D(0, 0, 1));
    private void ViewIso_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(0.5, 1, -0.8), new Vector3D(0, 0, 1));
    private void ViewFit_Click(object sender, RoutedEventArgs e) => ActiveViewport.ZoomExtents(300);
    private SpeedWindow? _speedWindow;
    private void OpenSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (_speedWindow != null) { _speedWindow.Activate(); return; }
        _speedWindow = new SpeedWindow(_vm) { Owner = this };
        _speedWindow.Left = Left + Width - _speedWindow.Width - 40;
        _speedWindow.Top = Top + 150;
        _speedWindow.Closed += (_, _) => _speedWindow = null;
        _speedWindow.Show();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Play_Click(object sender, RoutedEventArgs e) => Cyclogram.FollowCursor = true;
    private void CycloFollow_Click(object sender, RoutedEventArgs e) => Cyclogram.FollowCursor = true;
    private void CycloFit_Click(object sender, RoutedEventArgs e) => Cyclogram.FitAll();

    /// <summary>Invio conferma il valore della proprietà senza dover cambiare campo.</summary>
    private void PropertyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
