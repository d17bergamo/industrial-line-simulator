using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LineSimulator.App.Controls;
using LineSimulator.App.Mvvm;
using LineSimulator.App.ViewModels;

namespace LineSimulator.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>Vista 3D della sezione aperta (layout o simulazioni).</summary>
    private HelixToolkit.Wpf.HelixViewport3D ActiveViewport => LayoutViewport;
    private Rendering.LineScene ActiveScene => _vm.LayoutScene;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
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
        PackageViewport.Children.Add(_packageScene.Visual);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.PackagePreviewVersion) or nameof(MainViewModel.SelectedPackageType) or nameof(MainViewModel.SelectedStrapIndex)) RefreshPackage3D();
        };
        RefreshPackage3D();
    }

    // ------------------------------------------------------------ Pacco in 3D con legature spostabili

    private readonly Rendering.PackageScene _packageScene = new();
    private LineSimulator.Models.PackageType? _lastPackage3D;
    private int _strapDrag = -1;

    private void RefreshPackage3D()
    {
        var sel = _vm.SelectedPackageType;
        _packageScene.SelectedIndex = _vm.SelectedStrapIndex;
        _packageScene.Build(sel);
        if (sel == null || ReferenceEquals(sel, _lastPackage3D)) return;
        _lastPackage3D = sel;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PackageViewport.Camera.Position = new Point3D(-0.3, -3.0, 2.2);
            PackageViewport.Camera.LookDirection = new Vector3D(0.3, 3.0, -2.2);
            PackageViewport.Camera.UpDirection = new Vector3D(0, 0, 1);
            PackageViewport.ZoomExtents(200);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PackageViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pk = _vm.SelectedPackageType; if (pk == null) return;
        pk.StrapPositionsMm = pk.StrapPositionsMm.OrderBy(v => v).ToList();
        int idx = -1;
        if (VisualTreeHelper.HitTest(PackageViewport.Viewport, e.GetPosition(PackageViewport.Viewport)) is RayMeshGeometry3DHitTestResult hit)
            idx = _packageScene.PickStrap(hit.ModelHit);
        _vm.SelectedStrapIndex = idx;
        _strapDrag = idx;
        if (idx >= 0) { PackageViewport.CaptureMouse(); e.Handled = true; }
    }

    private void PackageViewport_MouseMove(object sender, MouseEventArgs e)
    {
        var pk = _vm.SelectedPackageType;
        if (pk == null || _strapDrag < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var p = PlanePoint(PackageViewport, e.GetPosition(PackageViewport.Viewport), _packageScene.HeightM);
        if (p == null) return;
        double s = LineSimulator.Core.StrapEditing.Snap((pk.LengthMm / 2.0) - p.Value.X * 1000.0);
        pk.StrapPositionsMm[_strapDrag] = Math.Clamp(s, LineSimulator.Core.StrapEditing.StepMm,
            Math.Max(LineSimulator.Core.StrapEditing.StepMm, pk.LengthMm - LineSimulator.Core.StrapEditing.StepMm));
        _packageScene.SelectedIndex = _strapDrag;
        _packageScene.Build(pk);
    }

    private void PackageViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_strapDrag < 0) return;
        _strapDrag = -1;
        PackageViewport.ReleaseMouseCapture();
        var pk = _vm.SelectedPackageType;
        if (pk != null)
        {
            double? sel = _vm.SelectedStrapIndex >= 0 && _vm.SelectedStrapIndex < pk.StrapPositionsMm.Count ? pk.StrapPositionsMm[_vm.SelectedStrapIndex] : null;
            LineSimulator.Core.StrapEditing.SnapAll(pk);
            _vm.SelectedStrapIndex = sel is double v ? pk.StrapPositionsMm.IndexOf(v) : -1;
        }
        _vm.OnStrapsEditedInPreview();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.CanClose()) e.Cancel = true;
    }

    /// <summary>Ctrl + clic su una voce della struttura: selezione multipla.</summary>
    private void TreeItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d != null && d is not TreeViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (!ReferenceEquals(d, sender)) return;                       // solo il nodo più interno
        if (((TreeViewItem)sender).DataContext is TreeNodeViewModel node)
        {
            _vm.ToggleMultiSelect(node);
            e.Handled = true;
        }
    }

    private void LineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeViewModel node) _vm.SelectedNode = node;
    }

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

    // ------------------------------------------------------------ Selezione e spostamento col mouse (layout e simulazione)

    private bool _moving;
    private double _lastXm;
    private HelixToolkit.Wpf.HelixViewport3D? _moveViewport;
    private Rendering.LineScene? _moveScene;

    /// <summary>Punto del piano dei rulli sotto il cursore (coordinate di scena, metri).</summary>
    private static Point3D? PlanePoint(HelixToolkit.Wpf.HelixViewport3D vp, Point pt, double z = 0)
        => HelixToolkit.Wpf.Viewport3DHelper.UnProject(vp.Viewport, pt, new Point3D(0, 0, z), new Vector3D(0, 0, 1));

    private void BeginMove(HelixToolkit.Wpf.HelixViewport3D viewport, Rendering.LineScene scene, MouseButtonEventArgs e)
    {
        var obj = PickAt(viewport, scene, e);
        if (obj == null || !MainViewModel.CanMove(obj)) return;
        var p = PlanePoint(viewport, e.GetPosition(viewport.Viewport));
        if (p == null) return;
        if (_vm.IsPlaying) _vm.PauseCommand.Execute(null);        // si modifica a simulazione ferma: al rilascio riparte da zero
        _moving = true;
        _moveViewport = viewport;
        _moveScene = scene;
        scene.Dragging = true;
        _lastXm = p.Value.X;
        viewport.CaptureMouse();
    }

    private void LayoutViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginMove(LayoutViewport, _vm.LayoutScene, e);

    private void MoveViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_moving || _moveViewport == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = PlanePoint(_moveViewport, e.GetPosition(_moveViewport.Viewport));
        if (p == null) return;
        double dx = (p.Value.X - _lastXm) * 1000.0;
        if (Math.Abs(dx) < 1) return;
        _lastXm = p.Value.X;
        _vm.MoveSelectedBy(dx, p.Value.Y);
    }

    private void MoveViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_moving) return;
        _moving = false;
        if (_moveScene != null) _moveScene.Dragging = false;
        _moveViewport?.ReleaseMouseCapture();
        _moveViewport = null;
        _moveScene = null;
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
        var p = PlanePoint(LayoutViewport, e.GetPosition(LayoutViewport.Viewport));
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
        var p = PlanePoint(LayoutViewport, e.GetPosition(LayoutViewport.Viewport));
        if (p == null) return;
        _vm.PlaceFromLibrary(entry, p.Value.X * 1000.0, p.Value.Y);
        e.Handled = true;
    }

    private void AddMenu_Click(object sender, RoutedEventArgs e)
    {
        var btn = (FrameworkElement)sender;
        if (btn.ContextMenu == null) return;
        btn.ContextMenu.DataContext = _vm;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu.IsOpen = true;
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
