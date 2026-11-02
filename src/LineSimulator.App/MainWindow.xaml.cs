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
    private void LayoutViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => PickAt(LayoutViewport, _vm.LayoutScene, e);

    private void PickAt(HelixToolkit.Wpf.HelixViewport3D viewport, Rendering.LineScene scene, MouseButtonEventArgs e)
    {
        var vp = viewport.Viewport;
        var pt = e.GetPosition(vp);
        if (VisualTreeHelper.HitTest(vp, pt) is RayMeshGeometry3DHitTestResult hit && hit.ModelHit != null)
        {
            var obj = scene.Pick(hit.ModelHit);
            if (obj != null) _vm.SelectModel(obj);
        }
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
