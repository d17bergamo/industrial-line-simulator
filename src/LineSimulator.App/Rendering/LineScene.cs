using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.App.Rendering;

/// <summary>
/// Scena 3D parametrica della linea. Sistema: X = asse linea (m), Y = laterale (+Y = sinistra nel verso del flusso),
/// Z = alto. La parte statica è ricostruita da zero a ogni modifica dei parametri; la parte dinamica
/// (pacchi, fotocellule, legatrici, trasferitore, zone) è aggiornata da un <see cref="SimFrame"/> del motore.
/// </summary>
public sealed class LineScene
{
    // Macchine SUND BIRSTA (legatrice KNCA, stringifascio KNOF, rigettatrice SMA): modelli semplificati in grigio neutro.
    // Macchine di legatura/stringifascio/compattatore: grigio a riposo, verde in lavoro, rosso in guasto, arancione se selezionate.
    private static readonly Color MachWorking = Color.FromRgb(0x2E, 0x9E, 0x50);
    private static readonly Color MachBlue = Color.FromRgb(0x8C, 0x92, 0x98);          // telai (grigio normale)
    private static readonly Color MachBlueActive = MachWorking;
    private static readonly Color MachYellow = Color.FromRgb(0x8C, 0x92, 0x98);        // parti mobili (grigio normale)
    private static readonly Color MachYellowActive = MachWorking;
    private static readonly Color MachLightGrey = Color.FromRgb(0xB5, 0xBA, 0xBF);
    private static readonly Color MachMidGrey = Color.FromRgb(0x6F, 0x76, 0x7C);
    private static readonly Color MachBlack = Color.FromRgb(0x4A, 0x4F, 0x54);
    private static readonly Color GreyBody = Color.FromRgb(0x8C, 0x92, 0x98);
    private static readonly Color GreyMoving = Color.FromRgb(0xB5, 0xBA, 0xBF);
    private static readonly Color GreyOff = Color.FromRgb(0xC8, 0xCB, 0xCE);
    private static readonly Color FaultRed = Color.FromRgb(0xC0, 0x39, 0x2B);

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x2E, 0x86, 0xC1), Color.FromRgb(0x28, 0xB4, 0x63), Color.FromRgb(0xAF, 0x7A, 0xC5),
        Color.FromRgb(0xE6, 0x7E, 0x22), Color.FromRgb(0x16, 0xA0, 0x85), Color.FromRgb(0xC0, 0x39, 0x2B)
    };

    private readonly Model3DGroup _group = new();
    private readonly Dictionary<Model3D, object> _pick = new();
    private readonly Dictionary<object, List<(SolidColorBrush Brush, Color Normal)>> _brushesByObject = new();
    private readonly Dictionary<string, SolidColorBrush> _beamBrushes = new();
    private readonly Dictionary<string, SolidColorBrush> _strapperBrushes = new();
    private readonly Dictionary<string, (SolidColorBrush Brush, Color Color)> _zoneBrushes = new();
    private readonly Dictionary<string, (GeometryModel3D Model, TranslateTransform3D T, bool Strapped)> _packages = new();
    private readonly Dictionary<string, GeometryModel3D> _packageStraps = new();
    private static readonly Color StrapColor = Color.FromRgb(0x1A, 0x1D, 0x21);
    /// <summary>Macchina ferma in attesa di un consenso.</summary>
    public static readonly Color WaitAmber = Color.FromRgb(0xF2, 0xA9, 0x00);
    private static readonly Color[] PackagePalette =
    {
        Color.FromRgb(0x2F, 0x6F, 0xD0), Color.FromRgb(0xD9, 0x82, 0x2B), Color.FromRgb(0x7A, 0x52, 0xB3),
        Color.FromRgb(0x11, 0x76, 0x6A), Color.FromRgb(0xC2, 0x41, 0x7A), Color.FromRgb(0x6B, 0x7F, 0x1A)
    };
    /// <summary>Colore del pacco in simulazione secondo la posizione della tipologia nell'elenco (stessa legenda della barra risultati).</summary>
    public static Color PackageColor(int typeIndex) => typeIndex < 0 ? Color.FromRgb(0x85, 0x92, 0x9E) : PackagePalette[typeIndex % PackagePalette.Length];
    private readonly Dictionary<string, (MeshGeometry3D Plain, MeshGeometry3D Strapped)> _packageMeshes = new();
    private readonly List<string> _zoneOrder = new();
    private Model3DGroup _packagesGroup = new();
    private TranslateTransform3D _transferT = new();
    private SolidColorBrush? _stopperBrush;
    private SolidColorBrush? _transferBrush;
    private SolidColorBrush? _knofBrush, _compactorBrush, _smaBrush;
    private readonly TranslateTransform3D _knofT = new(), _jawLeftT = new(), _jawRightT = new(), _smaT = new();
    private readonly List<(SolidColorBrush Brush, TranslateTransform3D T, IntermediateStopConfig Cfg)> _stopVisuals = new();
    private readonly List<(SolidColorBrush Brush, WeighingScaleConfig Cfg)> _scaleVisuals = new();
    private double _smaSideSign = 1;
    private object? _highlighted;
    private LineProject? _project;
    private double _zTop;
    private double _lineWidth = 1.2;

    private readonly ModelVisual3D _overlay = new();
    private readonly ModelVisual3D _ghost = new();
    private readonly ModelVisual3D _dims = new();
    private readonly ModelVisual3D _names = new();
    private bool _showDimensions;

    public LineScene()
    {
        Visual = new ModelVisual3D { Content = _group };
        Visual.Children.Add(_overlay);
        _overlay.Children.Add(_ghost);
        _overlay.Children.Add(_dims);
        _overlay.Children.Add(_names);
    }

    /// <summary>Quote delle distanze dall'origine (X = Y = Z = 0) per ogni equipaggiamento.</summary>
    public bool ShowDimensions
    {
        get => _showDimensions;
        set { _showDimensions = value; RebuildDimensions(); }
    }

    /// <summary>Anteprima trasparente durante il trascinamento dalla libreria, con punto di inserimento e coordinata.</summary>
    public void ShowGhost(double xM, double yM, double lenM, double widM, double heightM)
    {
        var f = new MeshFactory();
        f.AddBox(new Point3D(xM, yM, heightM / 2), Math.Max(0.05, lenM), Math.Max(0.05, widM), Math.Max(0.05, heightM));
        var brush = new SolidColorBrush(Color.FromArgb(110, 0x1F, 0x6F, 0xB2));
        var mat = new DiffuseMaterial(brush);
        var body = new GeometryModel3D(f.ToMesh(), mat) { BackMaterial = mat };
        var m = new MeshFactory();
        m.AddBox(new Point3D(xM, yM, (_zTop + 1.2) / 2), 0.04, 0.04, _zTop + 1.2);       // punto di inserimento
        m.AddBox(new Point3D(xM, yM, 0.01), 0.6, 0.04, 0.02);
        m.AddBox(new Point3D(xM, yM, 0.01), 0.04, 0.6, 0.02);
        var marker = MeshFactory.Model(m.ToMesh(), Color.FromRgb(0xC0, 0x39, 0x2B), out _);
        _ghost.Children.Clear();
        _ghost.Content = new Model3DGroup { Children = { body, marker } };
        _ghost.Children.Add(Label($"X = {xM.ToString("0.000", CultureInfo.InvariantCulture)} m   Y = {yM.ToString("0.000", CultureInfo.InvariantCulture)} m",
            new Point3D(xM, yM, _zTop + 1.4)));
    }

    public void HideGhost()
    {
        _ghost.Content = null;
        _ghost.Children.Clear();
    }

    private static BillboardTextVisual3D Label(string text, Point3D pos) => new()
    {
        Text = text,
        Position = pos,
        FontSize = 11,
        Foreground = Brushes.Black,
        Background = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)),
        Padding = new System.Windows.Thickness(3, 1, 3, 1)
    };

    private bool _dragging;
    /// <summary>Durante lo spostamento col mouse le quote tra le macchine si vedono comunque.</summary>
    public bool Dragging
    {
        get => _dragging;
        set { if (_dragging == value) return; _dragging = value; RebuildDimensions(); }
    }

    private void RebuildDimensions()
    {
        _dims.Children.Clear();
        if (!_showDimensions || _project == null) return;
        double refX = LayoutGeometry.EntryReferenceM(_project);          // origine delle quote: fotocellula di ingresso
        var info = LayoutGeometry.Describe(_project)
            .Where(i => i.Kind is not ("Fotocellula" or "Fermo scomparente"))
            .OrderBy(i => i.CenterXm).ToList();
        double w = _lineWidth, z = 0.03, laneY0 = -(w / 2 + 1.2);
        int lanes = 4;
        double yMax = laneY0 - 0.7 * (lanes - 1);
        var lines = new LinesVisual3D { Color = Color.FromRgb(0x5A, 0x66, 0x72), Thickness = 1 };
        var pts = new Point3DCollection();
        void Seg(Point3D a, Point3D b) { pts.Add(a); pts.Add(b); }
        // asse di riferimento = fotocellula di ingresso
        Seg(new Point3D(refX, yMax - 0.3, z), new Point3D(refX, w / 2 + 0.3, z));
        _dims.Children.Add(Label($"fotocellula di ingresso (X = {Fmt(refX)} m): quote da qui", new Point3D(refX, yMax - 0.5, z)));
        for (int i = 0; i < info.Count; i++)
        {
            var it = info[i];
            double ly = laneY0 - 0.7 * (i % lanes);
            Seg(new Point3D(refX, ly, z), new Point3D(it.CenterXm, ly, z));                      // quota dalla fotocellula
            Seg(new Point3D(it.CenterXm, ly, z), new Point3D(it.CenterXm, it.YM, z));            // richiamo al centro macchina
            Seg(new Point3D(it.EntryXm, ly - 0.08, z), new Point3D(it.EntryXm, ly + 0.08, z));   // tacca ingresso
            Seg(new Point3D(it.ExitXm, ly - 0.08, z), new Point3D(it.ExitXm, ly + 0.08, z));     // tacca uscita
            string t = $"{it.Id}: C {Fmt(it.CenterXm - refX)}  IN {Fmt(it.EntryXm - refX)}  OUT {Fmt(it.ExitXm - refX)} m";
            if (Math.Abs(it.YM) > 1e-3) t += $"  Y {Fmt(it.YM)}";
            if (Math.Abs(it.ZM) > 1e-3 && Math.Abs(it.ZM - _zTop) > 1e-3) t += $"  Z {Fmt(it.ZM)}";
            _dims.Children.Add(Label(t, new Point3D(it.CenterXm, ly - 0.2, z)));
        }
        // distanze tra macchine adiacenti, sopra la linea
        var byId = info.ToDictionary(x => x.Id);
        double gy = w / 2 + 0.9;
        foreach (var g in LayoutGeometry.Gaps(_project))
        {
            if (!byId.TryGetValue(g.FromId, out var a) || !byId.TryGetValue(g.ToId, out var b)) continue;
            Seg(new Point3D(a.ExitXm, gy, z), new Point3D(b.EntryXm, gy, z));
            Seg(new Point3D(a.ExitXm, gy - 0.08, z), new Point3D(a.ExitXm, gy + 0.08, z));
            Seg(new Point3D(b.EntryXm, gy - 0.08, z), new Point3D(b.EntryXm, gy + 0.08, z));
            _dims.Children.Add(Label($"{g.FromId}-{g.ToId}: {Fmt(g.ClearM)} m", new Point3D((a.ExitXm + b.EntryXm) / 2, gy + 0.15, z)));
        }
        lines.Points = pts;
        _dims.Children.Add(lines);
    }

    private static string Fmt(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    public ModelVisual3D Visual { get; }
    public Rect3D Bounds { get; private set; }

    public object? Pick(Model3D model) => _pick.TryGetValue(model, out var o) ? o : null;

    public void Build(LineProject p)
    {
        _project = p;
        _group.Children.Clear();
        _pick.Clear();
        _brushesByObject.Clear();
        _beamBrushes.Clear();
        _strapperBrushes.Clear();
        _strapperMoveT.Clear();
        _strapperIsSma.Clear();
        _zoneBrushes.Clear();
        _packages.Clear();
        _packageStraps.Clear();
        _packageMeshes.Clear();
        _zoneOrder.Clear();
        _sectionFrameBrushes.Clear(); _sectionRollerBrushes.Clear();
        _stopVisuals.Clear();
        _scaleVisuals.Clear();
        _knofBrush = _knofBrushR = _compactorBrush = _smaBrush = null;
        _knofArmBrushes.Clear();
        _knofs.Clear();
        _knofEnabled.Clear();
        _highlighted = null;
        _names.Children.Clear();

        var topo = new LineTopology(p);
        _zoneOrder.AddRange(topo.Zones.Select(z => z.Id));
        _zTop = p.Layout.RollerTopElevationMm / 1000.0;
        _lineWidth = p.Sections.Count > 0 ? p.Sections.Max(s => s.WidthMm) / 1000.0 : 1.2;

        var tg = new Transform3DGroup();
        tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), p.Layout.RotationDeg)));
        tg.Children.Add(new TranslateTransform3D(p.Layout.OriginXMm / 1000.0, p.Layout.OriginYMm / 1000.0, 0));
        _group.Transform = tg;

        double x0 = topo.LineStartM - 2, x1 = topo.LineEndM + 2;

        // Un colore per modulo di via a rulli (ogni modulo ha il proprio inverter)
        var ordered = p.Sections.OrderBy(x => x.StartMm).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i].IsEnabled ? GreyBody : GreyOff;        // tutte le vie a rulli dello stesso grigio (disabilitate: grigio chiaro)
            _zoneBrushes[ordered[i].Id] = (new SolidColorBrush(c), c);
        }

        foreach (var s in p.Sections) AddSection(s);
        foreach (var pc in p.Photocells) AddPhotocell(pc);
        foreach (var st in p.Strappers) AddStrapper(st);
        AddStopper(p.Stopper);
        AddTransfer(p.Transfer);
        if (p.AccumulationTransfer.IsEnabled) AddAccumulation(p.AccumulationTransfer, p.Transfer);
        // Uno stringifascio per ogni legatrice/reggettatrice (se previsto) oppure quello unico; si chiudono insieme
        // anche le macchine disabilitate restano disegnate, in grigio chiaro (colore delle macchine non in lavoro)
        var kPos = LayoutRules.BundleClampPositions(p).Select(k => k.PositionMm).ToList();
        if (p.BundleClamp.PositionMm > 0 || p.BundleClamp.PerMachine)
        {
            var all = !p.BundleClamp.PerMachine ? new List<(double X, bool On)> { (p.BundleClamp.PositionMm, p.BundleClamp.IsEnabled) }
                : p.Strappers.OrderBy(s => s.PositionMm).Select(s => (s.PositionMm - p.BundleClamp.MachineOffsetMm, p.BundleClamp.IsEnabled && s.IsEnabled)).ToList();
            foreach (var (xMm, on) in all)
            {
                AddBundleClamp(p.BundleClamp, xMm);
                _knofEnabled.Add(on);
                if (!on) foreach (var b in _knofs[^1].Brushes) b.Color = GreyOff;
            }
        }
        if (p.Compactor.IsEnabled || p.Compactor.PositionMm > 0) AddCompactor(p.Compactor);
        if (p.Rejector.IsEnabled) AddRejector(p.Rejector, p.Transfer);
        foreach (var fs in p.IntermediateStops)
        {
            AddIntermediateStop(fs);
            if (!fs.IsEnabled) { _stopVisuals.RemoveAt(_stopVisuals.Count - 1); Dim(fs); }
        }
        foreach (var sc in p.Scales)
        {
            AddScale(sc, p.Transfer);
            if (!sc.IsEnabled) { _scaleVisuals.RemoveAt(_scaleVisuals.Count - 1); Dim(sc); }
        }
        if (!p.Compactor.IsEnabled) Dim(p.Compactor);
        if (!p.BundleClamp.IsEnabled) Dim(p.BundleClamp);

        // Pavimento: largo quanto tutte le macchine coinvolte, più un margine
        var mb = Rect3D.Empty;
        foreach (var ch in _group.Children) mb.Union(ch.Bounds);
        if (mb.IsEmpty) mb = new Rect3D(x0, -3, 0, x1 - x0, 6, 1);
        double fx0 = Math.Min(x0, mb.X - 2), fx1 = Math.Max(x1, mb.X + mb.SizeX + 2), fy0 = mb.Y - 2, fy1 = mb.Y + mb.SizeY + 2;
        var floor = MeshFactory.Box(new Point3D((fx0 + fx1) / 2, (fy0 + fy1) / 2, -0.01), fx1 - fx0, fy1 - fy0, 0.02, Color.FromRgb(0xE8, 0xEB, 0xEE), out _);
        _group.Children.Insert(0, floor);
        x0 = fx0; x1 = fx1;

        _packagesGroup = new Model3DGroup();
        _group.Children.Add(_packagesGroup);

        Bounds = new Rect3D(x0, fy0, 0, x1 - x0, fy1 - fy0, 3);
        _overlay.Transform = tg;
        RebuildDimensions();
    }

    /// <summary>Componente disabilitato: tutte le sue parti in grigio chiaro (anche dopo la selezione).</summary>
    private void Dim(object owner)
    {
        if (!_brushesByObject.TryGetValue(owner, out var list)) return;
        for (int i = 0; i < list.Count; i++) { list[i].Brush.Color = GreyOff; list[i] = (list[i].Brush, GreyOff); }
    }

    private readonly List<bool> _knofEnabled = new();

    private void Register(Model3D m, object owner, SolidColorBrush brush, Color normal)
    {
        _pick[m] = owner;
        if (!_brushesByObject.TryGetValue(owner, out var list)) _brushesByObject[owner] = list = new();
        list.Add((brush, normal));
    }

    private void AddSection(RollerSection s)
    {
        double start = s.StartMm / 1000.0, len = s.LengthMm / 1000.0, w = s.WidthMm / 1000.0;
        double r = Math.Max(0.02, s.RollerDiameterMm / 2000.0);
        var zc = _zoneBrushes.TryGetValue(s.Id, out var zb) ? zb : (new SolidColorBrush(Colors.Gray), Colors.Gray);

        // Struttura: longheroni e gambe
        var frame = new MeshFactory();
        double zBeam = _zTop - 2 * r - 0.08;
        frame.AddBox(new Point3D(start + len / 2, w / 2 + 0.05, zBeam), len, 0.08, 0.16);
        frame.AddBox(new Point3D(start + len / 2, -w / 2 - 0.05, zBeam), len, 0.08, 0.16);
        int legs = Math.Max(2, (int)Math.Ceiling(len / 3.0) + 1);
        for (int i = 0; i < legs; i++)
        {
            double x = start + 0.1 + (len - 0.2) * i / (legs - 1);
            frame.AddBox(new Point3D(x, w / 2 + 0.05, zBeam / 2), 0.08, 0.08, zBeam);
            frame.AddBox(new Point3D(x, -w / 2 - 0.05, zBeam / 2), 0.08, 0.08, zBeam);
        }
        var frameModel = MeshFactory.Model(frame.ToMesh(), zc.Item2, out var fb);
        _group.Children.Add(frameModel);
        Register(frameModel, s, fb, zc.Item2);
        if (_zoneBrushes.ContainsKey(s.Id))
        {
            // il colore della struttura indica lo stato della zona (vedi ApplyFrame)
            _sectionFrameBrushes.Add((s.EffectiveZoneId, fb, zc.Item2)); // stato della zona di comando (inverter)
        }

        // Rulli
        var rollers = new MeshFactory();
        int n = Math.Max(1, s.RollerCount);
        double used = (n - 1) * s.RollerPitchMm / 1000.0;
        double first = start + Math.Max(0, (len - used) / 2);
        for (int i = 0; i < n; i++)
            rollers.AddCylinderY(first + i * s.RollerPitchMm / 1000.0, _zTop - r, -w / 2, w / 2, r, 10);
        var rollColor = s.IsEnabled ? Color.FromRgb(0x9A, 0xA4, 0xAE) : GreyOff;
        var rm = MeshFactory.Model(rollers.ToMesh(), rollColor, out var rb);
        _group.Children.Add(rm);
        Register(rm, s, rb, rollColor);
        _sectionRollerBrushes.Add((s.EffectiveZoneId, s, rb, rollColor));

        // Sponde laterali alte 500 mm solo sulla via rulli di legatura (quella che contiene le legatrici, altrimenti la prima)
        if (IsStrappingSection(s))
        {
            var rails = new MeshFactory();
            rails.AddBox(new Point3D(start + len / 2, w / 2 + 0.04, _zTop + 0.25), len, 0.04, 0.5);
            rails.AddBox(new Point3D(start + len / 2, -w / 2 - 0.04, _zTop + 0.25), len, 0.04, 0.5);
            var railColor = Color.FromRgb(0xC9, 0xD1, 0xDA);
            var railModel = MeshFactory.Model(rails.ToMesh(), railColor, out var railBrush);
            _group.Children.Add(railModel);
            Register(railModel, s, railBrush, railColor);
        }
    }

    /// <summary>Via rulli di legatura: quella che contiene almeno una legatrice, altrimenti la prima della linea.</summary>
    private bool IsStrappingSection(RollerSection s)
    {
        if (_project == null) return false;
        var withStrapper = _project.Sections.Where(x => _project.Strappers.Any(st => st.PositionMm >= x.StartMm - 1e-6 && st.PositionMm < x.EndMm)).ToList();
        if (withStrapper.Count > 0) return withStrapper.Contains(s);
        return ReferenceEquals(s, _project.Sections.OrderBy(x => x.StartMm).FirstOrDefault());
    }

    private readonly List<(string Inv, SolidColorBrush Brush, Color Color)> _sectionFrameBrushes = new();
    private readonly List<(string Inv, RollerSection Section, SolidColorBrush Brush, Color Color)> _sectionRollerBrushes = new();

    private void AddPhotocell(PhotocellConfig pc)
    {
        double x = pc.PositionMm / 1000.0, w = _lineWidth;
        var postColor = Color.FromRgb(0x34, 0x49, 0x5E);
        var f = new MeshFactory();
        // davanti al trasferitore (sulla sua via) la fotocellula è a tasteggio: una sola colonna, dal lato opposto al trasferitore
        bool diffuse = _project != null && LayoutRules.TransferModule(_project) is { } tm && pc.PositionMm >= tm.StartMm - 1e-6 && pc.PositionMm <= tm.EndMm + 1e-6;
        double away = _project?.Transfer.DestinationSide == LateralSide.Left ? -1 : 1;
        if (!diffuse || away < 0) f.AddBox(new Point3D(x, -w / 2 - 0.3, _zTop / 2 + 0.15), 0.06, 0.06, _zTop + 0.3);
        if (!diffuse || away > 0) f.AddBox(new Point3D(x, w / 2 + 0.3, _zTop / 2 + 0.15), 0.06, 0.06, _zTop + 0.3);
        var post = MeshFactory.Model(f.ToMesh(), postColor, out var pb);
        _group.Children.Add(post);
        Register(post, pc, pb, postColor);

        double beamZ = pc.Direction == DetectionDirection.Vertical ? _zTop + 0.6 : _zTop + 0.15;
        var beamColor = pc.IsEnabled ? Color.FromRgb(0x7D, 0xCE, 0xA0) : Color.FromRgb(0xAA, 0xAA, 0xAA);
        SolidColorBrush bb;
        GeometryModel3D beam;
        if (pc.Direction == DetectionDirection.Vertical)
            beam = MeshFactory.Box(new Point3D(x, 0, _zTop + 0.35), 0.02, 0.02, 0.7, beamColor, out bb);
        else if (diffuse)   // a tasteggio: il fascio parte dalla colonna e arriva al pacco (metà linea)
            beam = MeshFactory.Box(new Point3D(x, away * (w / 2 + 0.3) / 2, beamZ), 0.02, w / 2 + 0.3, 0.02, beamColor, out bb);
        else
            beam = MeshFactory.Box(new Point3D(x, 0, beamZ), 0.02, w + 0.6, 0.02, beamColor, out bb);
        _group.Children.Add(beam);
        Register(beam, pc, bb, beamColor);
        _beamBrushes[pc.Id] = bb;
    }

    private readonly Dictionary<string, TranslateTransform3D> _strapperMoveT = new();
    private readonly Dictionary<string, bool> _strapperIsSma = new();

    private GeometryModel3D Part(MeshFactory f, Color c, object owner, Transform3D? t = null)
    {
        var m = MeshFactory.Model(f.ToMesh(), c, out var b);
        if (t != null) m.Transform = t;
        _group.Children.Add(m);
        Register(m, owner, b, c);
        _lastPartBrush = b;
        return m;
    }
    private SolidColorBrush? _lastPartBrush;

    /// <summary>
    /// Legatrice (modelli semplificati dal documento). KNCA: telaio blu su basamento, armadio grigio chiaro, grande anello blu
    /// aperto verso il pacco, scivolo blu in alto, tubi neri; l'anello scende durante la legatura.
    /// SMA: colonna e telaio blu su due travi, bobina gialla a sinistra, arco giallo a destra, testa grigia con tubi neri,
    /// armadio grigio; l'arco scorre sulla colonna.
    /// </summary>
    private void AddStrapper(StrapperConfig st)
    {
        bool sma = st.MachineType == StrapperMachineType.SMA;
        double x = st.PositionMm / 1000.0, w = _lineWidth, fp = Math.Max(0.3, st.FootprintLengthMm / 1000.0);
        double side = st.Side == LateralSide.Left ? 1 : -1;
        double yc = side * (w / 2 + 0.55);              // asse del telaio, lato macchina
        double top = _zTop + 1.3;
        var move = new TranslateTransform3D();
        _strapperMoveT[st.Id] = move;
        _strapperIsSma[st.Id] = sma;
        Color blue = st.IsEnabled ? MachBlue : GreyOff;

        if (!sma)
        {
            var frame = new MeshFactory();
            frame.AddBox(new Point3D(x, yc, 0.15), fp, 0.9, 0.3);                                     // basamento con slitta
            frame.AddBox(new Point3D(x, yc, 0.4), fp * 0.8, 0.5, 0.2);                                // slitta di traslazione
            frame.AddBox(new Point3D(x, side * (w / 2 + 0.25), top / 2), 0.3, 0.3, top);               // montante
            Part(frame, blue, st);
            var cab = new MeshFactory();
            cab.AddBox(new Point3D(x - fp * 0.25, yc + side * 0.55, 0.65), Math.Min(0.7, fp * 0.5), 0.45, 1.3);    // armadio elettrico
            Part(cab, MachLightGrey, st);
            // anello di legatura (si abbassa in lavoro): lati, base e traversa; aperto verso il pacco sul lato macchina
            var ring = new MeshFactory();
            double rh = 1.2, rz = _zTop + rh / 2 - 0.1;
            ring.AddBox(new Point3D(x, 0, _zTop + rh - 0.05), 0.22, w + 0.5, 0.14);                   // traversa superiore
            ring.AddBox(new Point3D(x, -side * (w / 2 + 0.2), rz), 0.22, 0.14, rh);                    // lato opposto
            ring.AddBox(new Point3D(x, side * (w / 2 + 0.2), rz + 0.35), 0.22, 0.14, rh - 0.7);        // lato macchina (aperto in basso)
            ring.AddBox(new Point3D(x, 0, _zTop - 0.18), 0.22, w + 0.5, 0.1);                          // catena inferiore
            Part(ring, blue, st, move);
            _strapperBrushes[st.Id] = _lastPartBrush!;
            var chute = new MeshFactory();
            chute.AddBox(new Point3D(x, side * (w / 2 + 0.3), top + 0.1), 0.25, 0.5, 0.1);             // scivolo inclinato (a gradini)
            chute.AddBox(new Point3D(x, side * (w / 2 + 0.55), top + 0.25), 0.25, 0.4, 0.1);
            Part(chute, blue, st);
            var hoses = new MeshFactory();
            hoses.AddBox(new Point3D(x + 0.15, side * (w / 2 + 0.3), _zTop + 0.3), 0.06, 0.06, 0.6);   // tubi e valvole neri
            hoses.AddBox(new Point3D(x - 0.15, side * (w / 2 + 0.3), _zTop + 0.5), 0.06, 0.06, 0.5);
            Part(hoses, MachBlack, st);
        }
        else
        {
            var beams = new MeshFactory();
            beams.AddBox(new Point3D(x - fp * 0.3, yc, 0.1), 0.25, 1.2, 0.2);                          // due travi di base
            beams.AddBox(new Point3D(x + fp * 0.3, yc, 0.1), 0.25, 1.2, 0.2);
            beams.AddBox(new Point3D(x, side * (w / 2 + 0.3), top / 2), 0.35, 0.3, top);               // colonna verticale
            Part(beams, blue, st);
            var cab = new MeshFactory();
            cab.AddBox(new Point3D(x, yc + side * 0.55, 0.6), Math.Min(0.7, fp * 0.5), 0.45, 1.2);
            Part(cab, MachLightGrey, st);
            var coil = new MeshFactory();
            coil.AddBox(new Point3D(x - fp * 0.3, yc, 1.0), 0.15, 0.9, 0.9);                           // bobina della reggia
            coil.AddBox(new Point3D(x - fp * 0.3, yc, 1.0), 0.2, 0.15, 0.15);                          // volantino
            Part(coil, MachYellow, st);
            // arco giallo con testa grigia e tubi neri: scorre in verticale sulla colonna
            var arch = new MeshFactory();
            double az = _zTop + 0.9;
            arch.AddBox(new Point3D(x, 0, az + 0.3), 0.2, w + 0.5, 0.12);                              // traversa dell'arco
            arch.AddBox(new Point3D(x, -side * (w / 2 + 0.2), az), 0.2, 0.12, 0.7);
            arch.AddBox(new Point3D(x, side * (w / 2 + 0.2), az), 0.2, 0.12, 0.7);
            Part(arch, MachYellow, st, move);
            _strapperBrushes[st.Id] = _lastPartBrush!;
            var head = new MeshFactory();
            head.AddBox(new Point3D(x, 0, az + 0.45), 0.3, 0.5, 0.2);
            Part(head, MachMidGrey, st, move);
            var hoses = new MeshFactory();
            hoses.AddBox(new Point3D(x, side * 0.3, az + 0.5), 0.05, 0.5, 0.05);
            Part(hoses, MachBlack, st, move);
        }
        var plane = new MeshFactory();
        plane.AddBox(new Point3D(x, 0, _zTop + 0.02), 0.03, w + 0.3, 0.03);                            // piano di legatura
        var pm = MeshFactory.Model(plane.ToMesh(), GreyOff, out var pb);
        _group.Children.Add(pm);
        Register(pm, st, pb, GreyOff);
    }

    private void AddStopper(StopperConfig s)
    {
        double x = s.PositionMm / 1000.0, h = Math.Max(0.05, s.GraphicHeightMm / 1000.0);
        var color = GreyBody;
        var m = MeshFactory.Box(new Point3D(x + 0.06, 0, _zTop - 0.1 + h / 2 + 0.05), 0.12, _lineWidth * 0.9, h + 0.1, color, out var b);
        _group.Children.Add(m);
        Register(m, s, b, color);
        _stopperBrush = b;
    }

    private void AddTransfer(TransferConfig t)
    {
        double x0 = t.PositionMm / 1000.0, len = t.LengthMm / 1000.0, stroke = t.TranslationStrokeMm / 1000.0;
        double sign = t.DestinationSide == LateralSide.Left ? 1 : -1;
        var color = GreyBody;
        var f = new MeshFactory();
        // una struttura per ogni sezione, separate da un piccolo vuoto; il nome compare sopra la sezione
        int forks = 0;
        double sx = x0;
        _names.Children.Clear();
        foreach (var sec in t.EffectiveSections())
        {
            double sl = Math.Max(0.3, sec.LengthMm / 1000.0), gap = 0.04;
            int n = Math.Max(2, (int)Math.Round(sl / 2.5) + 1);
            // trasferitore sollevabile a catene (unico tipo): catene trasversali tra i rulli, lunghe quanto la larghezza del trasferitore,
            // dal lato opposto della linea verso la destinazione, con i pignoni alle estremità
            double tw = Math.Max(_lineWidth + 0.2, t.WidthMm / 1000.0);
            double y0 = -sign * (_lineWidth / 2 + 0.1), y1 = y0 + sign * tw, yc = (y0 + y1) / 2;
            for (int i = 0; i < n; i++)
            {
                double x = sx + 0.1 + (sl - 0.2 - gap) * i / (n - 1);
                f.AddBox(new Point3D(x, yc, _zTop - 0.05), 0.06, tw, 0.05);
                f.AddBox(new Point3D(x, y0, _zTop - 0.05), 0.14, 0.14, 0.14);
                f.AddBox(new Point3D(x, y1, _zTop - 0.05), 0.14, 0.14, 0.14);
            }
            f.AddBox(new Point3D(sx + (sl - gap) / 2, 0, _zTop - 0.1), sl - gap, 0.1, 0.04);       // longherone di sezione
            forks += n;
            sx += sl;
        }
        var m = MeshFactory.Model(f.ToMesh(), color, out var b);
        _transferT = new TranslateTransform3D();
        m.Transform = _transferT;
        _group.Children.Add(m);
        Register(m, t, b, color);
        _transferBrush = b;

        // Guide di traslazione e stazione di destinazione
        var rails = new MeshFactory();
        for (int i = 0; i < Math.Max(2, forks); i++)
        {
            double x = x0 + 0.2 + (len - 0.4) * i / (Math.Max(2, forks) - 1);
            rails.AddBox(new Point3D(x, sign * stroke / 2, _zTop - 0.25), 0.1, stroke + _lineWidth, 0.06);
        }
        rails.AddBox(new Point3D(x0 + len / 2, sign * stroke, _zTop - 0.12), len, _lineWidth * 0.8, 0.04);
        var railColor = Color.FromRgb(0x7F, 0x8C, 0x8D);
        var rm = MeshFactory.Model(rails.ToMesh(), railColor, out var rb);
        _group.Children.Add(rm);
        Register(rm, t, rb, railColor);
    }

    /// <summary>Bracci di ogni stringifascio (uno per posizione): si chiude solo quello che ha il pacco sotto.</summary>
    private readonly List<(TranslateTransform3D L, TranslateTransform3D R, List<SolidColorBrush> Brushes)> _knofs = new();

    /// <summary>
    /// Stringifascio KNOF (modello semplificato): piastra blu a C aperta verso l'alto su trave a doppio T grigia,
    /// due bracci gialli in alto che chiudono lateralmente sul pacco, arco con golfari, quadro comandi.
    /// </summary>
    private void AddBundleClamp(BundleClampConfig k, double positionMm)
    {
        double x = positionMm / 1000.0, w = _lineWidth;
        double zb = _zTop - 0.35;
        var beam = new MeshFactory();
        beam.AddBox(new Point3D(x, 0, zb - 0.1), 0.3, w + 1.1, 0.2);                                  // trave a doppio T
        beam.AddBox(new Point3D(x, 0, zb - 0.25), 0.5, w + 1.1, 0.06);
        Part(beam, MachMidGrey, k);
        var c = new MeshFactory();
        c.AddBox(new Point3D(x, 0, zb + 0.04), 0.14, w + 0.7, 0.08);                                  // fondo della C
        c.AddBox(new Point3D(x, w / 2 + 0.3, zb + 0.75), 0.14, 0.08, 1.5);                             // montanti della C (aperta in alto)
        c.AddBox(new Point3D(x, -w / 2 - 0.3, zb + 0.75), 0.14, 0.08, 1.5);
        var cm = Part(c, MachBlue, k);
        var armL = new MeshFactory();
        armL.AddBox(new Point3D(x, w / 2 + 0.12, _zTop + 0.55), 0.18, 0.35, 0.14);                    // bracci gialli
        var armR = new MeshFactory();
        armR.AddBox(new Point3D(x, -w / 2 - 0.12, _zTop + 0.55), 0.18, 0.35, 0.14);
        var knof = (L: new TranslateTransform3D(), R: new TranslateTransform3D(), Brushes: new List<SolidColorBrush>());
        Part(armL, MachYellow, k, knof.L);
        _knofBrush = _lastPartBrush;
        if (_knofBrush != null) { _knofArmBrushes.Add(_knofBrush); knof.Brushes.Add(_knofBrush); }
        Part(armR, MachYellow, k, knof.R);
        _knofBrushR = _lastPartBrush;
        if (_knofBrushR != null) { _knofArmBrushes.Add(_knofBrushR); knof.Brushes.Add(_knofBrushR); }
        _knofs.Add(knof);
        var eye = new MeshFactory();
        eye.AddBox(new Point3D(x, w / 2 + 0.3, zb + 1.6), 0.1, 0.1, 0.1);                              // golfari uniti da un arco
        eye.AddBox(new Point3D(x, -w / 2 - 0.3, zb + 1.6), 0.1, 0.1, 0.1);
        eye.AddBox(new Point3D(x, 0, zb + 1.7), 0.06, w + 0.7, 0.05);
        Part(eye, MachMidGrey, k);
        var panel = new MeshFactory();
        panel.AddBox(new Point3D(x, w / 2 + 0.5, zb + 0.6), 0.2, 0.2, 0.3);                            // quadro comandi
        Part(panel, MachLightGrey, k);
    }

    private SolidColorBrush? _knofBrushR;
    /// <summary>Bracci di tutti gli stringifascio (uno per macchina): stesso stato, stesso colore.</summary>
    private readonly List<SolidColorBrush> _knofArmBrushes = new();

    /// <summary>Compattatore laterale: due ganasce che si chiudono verso l'asse linea.</summary>
    private void AddCompactor(SideCompactorConfig c)
    {
        double x = c.PositionMm / 1000.0, w = _lineWidth, len = Math.Max(0.3, c.JawLengthMm / 1000.0);
        var color = Color.FromRgb(0x8C, 0x92, 0x98);
        var left = MeshFactory.Box(new Point3D(x, w / 2 + 0.05, _zTop + 0.25), len, 0.12, 0.4, color, out var lb);
        left.Transform = _jawLeftT;
        var right = MeshFactory.Box(new Point3D(x, -w / 2 - 0.05, _zTop + 0.25), len, 0.12, 0.4, color, out var rb);
        right.Transform = _jawRightT;
        rb.Color = color;
        _group.Children.Add(left);
        _group.Children.Add(right);
        Register(left, c, lb, color);
        Register(right, c, rb, color);
        _compactorBrush = lb;
        _compactorRightBrush = rb;
    }

    private SolidColorBrush? _compactorRightBrush;

    /// <summary>Rigettatrice SMA: spintore lungo il campo di spinta e tavola scarti sul lato di espulsione.</summary>
    private void AddRejector(RejectorConfig r, TransferConfig t)
    {
        double x0 = r.PositionMm / 1000.0, len = Math.Max(0.5, r.LengthMm / 1000.0), w = _lineWidth;
        _smaSideSign = r.Side == LateralSide.Left ? 1 : -1;
        var color = GreyMoving;
        var body = new MeshFactory();
        body.AddBox(new Point3D(x0 + 0.4, -_smaSideSign * (w / 2 + 0.5), 0.5), 0.8, 0.5, 1.0);        // corpo macchina
        body.AddBox(new Point3D(x0 + 0.4, -_smaSideSign * (w / 2 + 0.3), (_zTop + 0.6) / 2), 0.3, 0.2, _zTop + 0.6); // montante
        var bm = MeshFactory.Model(body.ToMesh(), GreyBody, out var bbr);
        _group.Children.Add(bm);
        Register(bm, r, bbr, GreyBody);
        var pusher = MeshFactory.Box(new Point3D(x0 + len / 2, -_smaSideSign * (w / 2 + 0.15), _zTop + 0.25), len, 0.12, 0.3, color, out var pb);
        pusher.Transform = _smaT;
        _group.Children.Add(pusher);
        Register(pusher, r, pb, color);
        _smaBrush = pb;
        double stroke = Math.Max(0.5, r.StrokeMm / 1000.0);
        var table = new MeshFactory();
        int n = Math.Max(2, (int)Math.Round(len / 2.0) + 1);
        for (int i = 0; i < n; i++)
        {
            double x = x0 + 0.2 + (len - 0.4) * i / (n - 1);
            table.AddBox(new Point3D(x, _smaSideSign * (w / 2 + stroke / 2), _zTop - 0.05), 0.1, stroke, 0.06);
        }
        var tColor = GreyOff;
        var tm = MeshFactory.Model(table.ToMesh(), tColor, out var tb);
        _group.Children.Add(tm);
        Register(tm, r, tb, tColor);
    }

    /// <summary>Fermo scomparente: blocco che si solleva sopra il piano rulli quando armato.</summary>
    private void AddIntermediateStop(IntermediateStopConfig s)
    {
        double x = s.PositionMm / 1000.0, h = Math.Max(0.05, s.GraphicHeightMm / 1000.0);
        var color = Color.FromRgb(0x7F, 0x8C, 0x8D);
        var m = MeshFactory.Box(new Point3D(x + 0.05, 0, _zTop - h / 2), 0.1, _lineWidth * 0.5, h, color, out var b);
        var tt = new TranslateTransform3D();
        m.Transform = tt;
        _group.Children.Add(m);
        Register(m, s, b, color);
        _stopVisuals.Add((b, tt, s));
    }

    /// <summary>Bilico di pesatura sul lato di deposito del trasferitore.</summary>
    private void AddScale(WeighingScaleConfig sc, TransferConfig t)
    {
        double x0 = sc.StartMm / 1000.0, len = Math.Max(0.3, sc.LengthMm / 1000.0);
        double sign = t.DestinationSide == LateralSide.Left ? 1 : -1;
        // con il trasferitore a due prese il bilico sta a metà corsa
        double y = sign * t.TranslationStrokeMm / 1000.0 * (t.ScaleAtMidStroke ? 0.5 : 1.0);
        var color = GreyBody;
        var f = new MeshFactory();
        if (sc.Mounting == ScaleMounting.InLine)
        {
            // integrato nella via a rulli: telaio di pesata sotto i rulli, con le celle di carico agli angoli
            f.AddBox(new Point3D(x0 + len / 2, 0, _zTop - 0.22), len - 0.05, _lineWidth * 0.95, 0.05);
            foreach (double cx in new[] { x0 + 0.15, x0 + len - 0.15 })
                foreach (double cy in new[] { -_lineWidth * 0.4, _lineWidth * 0.4 })
                    f.AddBox(new Point3D(cx, cy, (_zTop - 0.25) / 2), 0.12, 0.12, _zTop - 0.25);
        }
        else
        {
            f.AddBox(new Point3D(x0 + len / 2, y, _zTop - 0.08), len - 0.05, _lineWidth * 0.9, 0.06);
            f.AddBox(new Point3D(x0 + len / 2, y, (_zTop - 0.1) / 2), len * 0.8, _lineWidth * 0.5, _zTop - 0.1);
        }
        var m = MeshFactory.Model(f.ToMesh(), color, out var b);
        _group.Children.Add(m);
        Register(m, sc, b, color);
        _scaleVisuals.Add((b, sc));
    }

    /// <summary>
    /// Trasferitore d'accumulo: catene trasversali che partono dal punto di deposito (fine corsa del trasferitore sollevabile)
    /// e si allungano per la larghezza utile, lungo tutto il trasferitore.
    /// </summary>
    private void AddAccumulation(AccumulationTransferConfig a, TransferConfig t)
    {
        double x0 = t.PositionMm / 1000.0, len = Math.Max(0.5, t.LengthMm / 1000.0);
        double sign = t.DestinationSide == LateralSide.Left ? 1 : -1;
        double y0 = t.TranslationStrokeMm / 1000.0 - _lineWidth / 2, w = Math.Max(0.5, a.WidthMm / 1000.0);
        var color = GreyBody;
        var f = new MeshFactory();
        int n = Math.Max(2, (int)Math.Round(len / 2.0) + 1);
        for (int i = 0; i < n; i++)
        {
            double x = x0 + 0.15 + (len - 0.3) * i / (n - 1);
            f.AddBox(new Point3D(x, sign * (y0 + w / 2), _zTop - 0.05), 0.08, w, 0.05);                 // catena trasversale
            f.AddBox(new Point3D(x, sign * (y0 + w / 2), (_zTop - 0.1) / 2), 0.12, 0.12, _zTop - 0.1);   // gamba centrale
        }
        f.AddBox(new Point3D(x0 + len / 2, sign * y0, _zTop - 0.12), len, 0.08, 0.06);                  // longheroni
        f.AddBox(new Point3D(x0 + len / 2, sign * (y0 + w), _zTop - 0.12), len, 0.08, 0.06);
        var m = MeshFactory.Model(f.ToMesh(), color, out var b);
        _group.Children.Add(m);
        Register(m, a, b, color);
    }

    private (MeshGeometry3D Plain, MeshGeometry3D Strapped) PackageMeshes(PackageSnapshot ps)
    {
        if (_packageMeshes.TryGetValue(ps.TypeId, out var m)) return m;
        double L = ps.LengthM, W = ps.GraphicWidthMm / 1000.0, H = ps.GraphicHeightMm / 1000.0;
        var plain = new MeshFactory();
        plain.AddBox(new Point3D(0, 0, 0), L, W, H);
        // legature / reggette: anelli neri attorno al pacco, con le stesse proporzioni della schermata Pacchi (spessore 60 mm, sporgenza 20 mm)
        var strapped = new MeshFactory();
        var type = _project?.PackageTypes.FirstOrDefault(t => t.Id == ps.TypeId);
        if (type != null)
            foreach (var d in type.StrapPositionsMm)
            {
                double x = L / 2 - d / 1000.0, t = 0.06, e = 0.02;
                strapped.AddBox(new Point3D(x, 0, H / 2 + e / 2), t, W + 2 * e, e);
                strapped.AddBox(new Point3D(x, 0, -H / 2 - e / 2), t, W + 2 * e, e);
                strapped.AddBox(new Point3D(x, W / 2 + e / 2, 0), t, e, H);
                strapped.AddBox(new Point3D(x, -W / 2 - e / 2, 0), t, e, H);
            }
        m = (plain.ToMesh(), strapped.ToMesh());
        _packageMeshes[ps.TypeId] = m;
        return m;
    }

    /// <summary>Aggiorna la parte dinamica dallo stato del motore (posizioni reali, non animazioni).</summary>
    public void ApplyFrame(SimFrame? f)
    {
        if (_project == null) return;
        var p = _project;
        var seen = new HashSet<string>();
        double sign = p.Transfer.DestinationSide == LateralSide.Left ? 1 : -1;
        if (f != null)
        {
            foreach (var ps in f.Packages)
            {
                if (ps.State == PackageState.Exited) continue;
                seen.Add(ps.Id);
                bool strapped = ps.StrapsTotal > 0 && ps.StrapsDone >= ps.StrapsTotal;
                var meshes = PackageMeshes(ps);
                if (!_packages.TryGetValue(ps.Id, out var entry))
                {
                    var model = MeshFactory.Model(meshes.Plain, PackageColor(p.PackageTypes.FindIndex(t => t.Id == ps.TypeId)), out _);   // colore per tipologia
                    var t = new TranslateTransform3D();
                    model.Transform = t;
                    _packagesGroup.Children.Add(model);
                    var strapModel = MeshFactory.Model(meshes.Strapped, StrapColor, out _);
                    strapModel.Transform = t;
                    strapModel.Geometry = strapped ? meshes.Strapped : new MeshGeometry3D();
                    _packagesGroup.Children.Add(strapModel);
                    _packageStraps[ps.Id] = strapModel;
                    entry = (model, t, strapped);
                    _packages[ps.Id] = entry;
                }
                if (entry.Strapped != strapped)
                {
                    if (_packageStraps.TryGetValue(ps.Id, out var sm)) sm.Geometry = strapped ? meshes.Strapped : new MeshGeometry3D();
                    entry = (entry.Model, entry.T, strapped);
                    _packages[ps.Id] = entry;
                }
                double H = ps.GraphicHeightMm / 1000.0;
                entry.T.OffsetX = ps.HeadM - ps.LengthM / 2;
                entry.T.OffsetY = sign * ps.LateralM;
                entry.T.OffsetZ = _zTop + H / 2 + ps.LiftM;
            }
        }
        foreach (var id in _packages.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _packagesGroup.Children.Remove(_packages[id].Model);
            if (_packageStraps.TryGetValue(id, out var gone)) { _packagesGroup.Children.Remove(gone); _packageStraps.Remove(id); }
            _packages.Remove(id);
        }

        // Fotocellule: rosso = interrotta
        for (int i = 0; i < p.Photocells.Count; i++)
        {
            var pc = p.Photocells[i];
            if (!_beamBrushes.TryGetValue(pc.Id, out var b) || IsHighlighted(pc)) continue;
            bool on = f != null && i < f.PhotocellOutputs.Length && f.PhotocellOutputs[i];
            b.Color = !pc.IsEnabled ? Color.FromRgb(0xAA, 0xAA, 0xAA) : on ? Color.FromRgb(0xE7, 0x4C, 0x3C) : Color.FromRgb(0x7D, 0xCE, 0xA0);
        }

        // Legatrici: colore per fase
        for (int i = 0; i < p.Strappers.Count; i++)
        {
            var st = p.Strappers[i];
            if (!_strapperBrushes.TryGetValue(st.Id, out var b) || IsHighlighted(st)) continue;
            var state = f != null && i < f.StrapperStates.Length ? f.StrapperStates[i] : StrapperState.Idle;
            // Grigio neutro: la fase si vede dal ciclogramma; in lavoro la macchina si schiarisce leggermente.
            bool isSma = _strapperIsSma.TryGetValue(st.Id, out var sm) && sm;
            var baseC = isSma ? MachYellow : MachBlue;
            var activeC = isSma ? MachYellowActive : MachBlueActive;
            b.Color = !st.IsEnabled ? GreyOff : state == StrapperState.Idle ? baseC : activeC;
            // anello (KNCA) o arco (SMA): scende durante la legatura e si rialza dopo
            if (_strapperMoveT.TryGetValue(st.Id, out var mt))
                mt.OffsetZ = state == StrapperState.Strapping ? -(isSma ? 0.55 : 0.3) : 0;
        }

        // Zone: struttura accesa se l'inverter è in moto
        foreach (var (inv, brush, color) in _sectionFrameBrushes)
        {
            int zi = _zoneOrder.IndexOf(inv);
            bool moving = f != null && zi >= 0 && zi < f.ZoneSpeeds.Length && f.ZoneSpeeds[zi] > 1e-4;
            if (_highlighted is RollerSection rs && rs.EffectiveZoneId == inv) continue;
            brush.Color = moving ? color : Blend(color, Color.FromRgb(0x55, 0x55, 0x55), 0.6);
        }

        // Rulli della via azionata: verdi
        foreach (var (inv, sec, rb, rc) in _sectionRollerBrushes)
        {
            int zi = _zoneOrder.IndexOf(inv);
            bool moving = f != null && zi >= 0 && zi < f.ZoneSpeeds.Length && f.ZoneSpeeds[zi] > 1e-4;
            if (ReferenceEquals(_highlighted, sec)) continue;
            rb.Color = moving ? MachWorking : rc;
        }

        // Trasferitore
        if (f != null)
        {
            _transferT.OffsetY = sign * f.TransferLateralM;
            _transferT.OffsetZ = f.TransferLiftM;
        }
        else { _transferT.OffsetY = 0; _transferT.OffsetZ = 0; }
        if (_stopperBrush != null && !IsHighlighted(p.Stopper))
            _stopperBrush.Color = f != null && f.StopperState != StopperState.Free ? MachWorking : GreyBody;
        if (_transferBrush != null && !IsHighlighted(p.Transfer))
            _transferBrush.Color = f != null && f.TransferState != TransferState.Home ? MachWorking : GreyBody;

        // Legatrici in attesa del consenso (stringifascio): giallo ambra
        for (int i = 0; i < p.Strappers.Count; i++)
        {
            if (f == null || i >= f.StrapperProcessStates.Length || f.StrapperProcessStates[i] != StrapperProcessState.WaitingClampConsent) continue;
            if (_strapperBrushes.TryGetValue(p.Strappers[i].Id, out var b) && !IsHighlighted(p.Strappers[i])) b.Color = WaitAmber;
        }

        // Legatrici KNCA in guasto: rosso
        for (int i = 0; i < p.Strappers.Count; i++)
        {
            if (f == null || i >= f.StrapperProcessStates.Length || f.StrapperProcessStates[i] != StrapperProcessState.Fault) continue;
            if (_strapperBrushes.TryGetValue(p.Strappers[i].Id, out var b) && !IsHighlighted(p.Strappers[i])) b.Color = FaultRed;
        }

        // Stringifascio: traversa che scende in chiusura
        // si chiude solo lo stringifascio con il pacco sotto (BundleClampActive); senza informazione si muovono tutti
        var active = f?.BundleClampActive ?? Array.Empty<bool>();
        var kc = (f?.BundleClampState ?? ClampState.Open) switch { ClampState.Fault => FaultRed, ClampState.Closed or ClampState.Closing or ClampState.Opening => MachWorking, _ => MachYellow };
        for (int i = 0, ai = 0; i < _knofs.Count; i++)
        {
            if (i < _knofEnabled.Count && !_knofEnabled[i]) continue;     // stringifascio disabilitato: fermo, grigio chiaro
            int enabledCount = _knofEnabled.Count(x => x);
            bool on = active.Length != enabledCount || active[ai];
            ai++;
            double arm = on ? (f?.BundleClampClosed ?? 0) * 0.3 : 0;
            _knofs[i].L.OffsetY = -arm;
            _knofs[i].R.OffsetY = arm;
            if (!IsHighlighted(p.BundleClamp))
                foreach (var b in _knofs[i].Brushes) b.Color = on ? kc : MachYellow;
        }

        // Compattatore: ganasce verso l'asse
        double jaw = (f?.CompactorClosed ?? 0) * 0.12;
        _jawLeftT.OffsetY = -jaw;
        _jawRightT.OffsetY = jaw;
        if (_compactorBrush != null && !IsHighlighted(p.Compactor) && p.Compactor.IsEnabled)
        {
            var cc = ClampColor(f?.CompactorState ?? ClampState.Open, Color.FromRgb(0x8C, 0x92, 0x98));
            _compactorBrush.Color = cc;
            if (_compactorRightBrush != null) _compactorRightBrush.Color = cc;
        }

        // Rigettatrice: spintore
        _smaT.OffsetY = _smaSideSign * (f?.RejectorStrokeM ?? 0);
        if (_smaBrush != null && !IsHighlighted(p.Rejector))
            _smaBrush.Color = (f?.RejectorState ?? RejectorState.Idle) switch
            {
                RejectorState.Fault => FaultRed,
                RejectorState.Idle => GreyMoving,
                _ => Blend(GreyMoving, GreyBody, 0.5)
            };

        // Fermi scomparenti: sollevati e colorati secondo lo stato
        for (int i = 0; i < _stopVisuals.Count; i++)
        {
            var (brush, tt, cfg) = _stopVisuals[i];
            bool raised = f != null && i < f.StopRaised.Length && f.StopRaised[i];
            var state = f != null && i < f.StopStates.Length ? f.StopStates[i] : StopState.Free;
            bool tailHold = cfg.Reference == StopReference.Tail && state is StopState.Aligning or StopState.Aligned;
            tt.OffsetZ = raised || tailHold ? Math.Max(0.05, cfg.GraphicHeightMm / 1000.0) * 0.9 : 0;
            if (IsHighlighted(cfg)) continue;
            brush.Color = state switch
            {
                StopState.Aligning => MachWorking,
                StopState.Aligned => MachWorking,
                StopState.Positioning or StopState.Assigned => MachWorking,
                _ => Color.FromRgb(0x7F, 0x8C, 0x8D)
            };
        }

        // Bilici
        for (int i = 0; i < _scaleVisuals.Count; i++)
        {
            var (brush, cfg) = _scaleVisuals[i];
            if (IsHighlighted(cfg)) continue;
            var state = f != null && i < f.ScaleStates.Length ? f.ScaleStates[i] : ScaleState.Idle;
            brush.Color = state switch
            {
                ScaleState.Idle => GreyBody,
                ScaleState.Stabilizing or ScaleState.Acquiring => Color.FromRgb(0xF4, 0xD0, 0x3F),
                ScaleState.Completed or ScaleState.Releasing => Color.FromRgb(0x28, 0xB4, 0x63),
                ScaleState.Fault => Color.FromRgb(0xC0, 0x39, 0x2B),
                _ => Color.FromRgb(0x5D, 0xAD, 0xE2)
            };
        }
    }

    private static Color ClampColor(ClampState s, Color idle) => s switch
    {
        ClampState.Closing or ClampState.Opening or ClampState.Closed => MachWorking,
        ClampState.Fault => Color.FromRgb(0xC0, 0x39, 0x2B),
        _ => idle
    };

    private bool IsHighlighted(object o) => ReferenceEquals(_highlighted, o);

    /// <summary>Evidenzia il componente selezionato (dal modello 3D o dall'albero).</summary>
    public void Highlight(object? obj)
    {
        if (_highlighted != null && _brushesByObject.TryGetValue(_highlighted, out var old))
            foreach (var (b, c) in old) b.Color = c;
        _highlighted = obj;
        if (obj != null && _brushesByObject.TryGetValue(obj, out var list))
            foreach (var (b, _) in list) b.Color = Color.FromRgb(0xFF, 0x8C, 0x00);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
