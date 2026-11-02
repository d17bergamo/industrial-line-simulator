using System.Windows.Media;
using System.Windows.Media.Media3D;
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

    public LineScene()
    {
        Visual = new ModelVisual3D { Content = _group };
    }

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
        _zoneBrushes.Clear();
        _packages.Clear();
        _packageMeshes.Clear();
        _zoneOrder.Clear();
        _sectionFrameBrushes.Clear();
        _stopVisuals.Clear();
        _scaleVisuals.Clear();
        _knofBrush = _compactorBrush = _smaBrush = null;
        _highlighted = null;

        var topo = new LineTopology(p);
        _zoneOrder.AddRange(topo.Zones.Select(z => z.Id));
        _zTop = p.Layout.RollerTopElevationMm / 1000.0;
        _lineWidth = p.Sections.Count > 0 ? p.Sections.Max(s => s.WidthMm) / 1000.0 : 1.2;

        var tg = new Transform3DGroup();
        tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), p.Layout.RotationDeg)));
        tg.Children.Add(new TranslateTransform3D(p.Layout.OriginXMm / 1000.0, p.Layout.OriginYMm / 1000.0, 0));
        _group.Transform = tg;

        // Pavimento
        double x0 = topo.LineStartM - 2, x1 = topo.LineEndM + 2;
        var floor = MeshFactory.Box(new Point3D((x0 + x1) / 2, -1.5, -0.01), x1 - x0, 9, 0.02, Color.FromRgb(0xE8, 0xEB, 0xEE), out _);
        _group.Children.Add(floor);

        // Un colore per modulo di via a rulli (ogni modulo ha il proprio inverter)
        var ordered = p.Sections.OrderBy(x => x.StartMm).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var c = Palette[i % Palette.Length];
            _zoneBrushes[ordered[i].Id] = (new SolidColorBrush(c), c);
        }

        foreach (var s in p.Sections) AddSection(s);
        foreach (var pc in p.Photocells) AddPhotocell(pc);
        foreach (var st in p.Strappers) AddStrapper(st);
        AddStopper(p.Stopper);
        AddTransfer(p.Transfer);
        if (p.BundleClamp.IsEnabled) AddBundleClamp(p.BundleClamp);
        if (p.Compactor.IsEnabled) AddCompactor(p.Compactor);
        if (p.Rejector.IsEnabled) AddRejector(p.Rejector, p.Transfer);
        foreach (var fs in p.IntermediateStops.Where(x => x.IsEnabled)) AddIntermediateStop(fs);
        foreach (var sc in p.Scales.Where(x => x.IsEnabled)) AddScale(sc, p.Transfer);

        _packagesGroup = new Model3DGroup();
        _group.Children.Add(_packagesGroup);

        Bounds = new Rect3D(x0, -5, 0, x1 - x0, 8, 3);
    }

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
            _sectionFrameBrushes.Add((s.Id, fb, zc.Item2));
        }

        // Rulli
        var rollers = new MeshFactory();
        int n = Math.Max(1, s.RollerCount);
        double used = (n - 1) * s.RollerPitchMm / 1000.0;
        double first = start + Math.Max(0, (len - used) / 2);
        for (int i = 0; i < n; i++)
            rollers.AddCylinderY(first + i * s.RollerPitchMm / 1000.0, _zTop - r, -w / 2, w / 2, r, 10);
        var rollColor = s.IsEnabled ? Color.FromRgb(0x9A, 0xA4, 0xAE) : Color.FromRgb(0x6B, 0x4B, 0x4B);
        var rm = MeshFactory.Model(rollers.ToMesh(), rollColor, out var rb);
        _group.Children.Add(rm);
        Register(rm, s, rb, rollColor);
    }

    private readonly List<(string Inv, SolidColorBrush Brush, Color Color)> _sectionFrameBrushes = new();

    private void AddPhotocell(PhotocellConfig pc)
    {
        double x = pc.PositionMm / 1000.0, w = _lineWidth;
        var postColor = Color.FromRgb(0x34, 0x49, 0x5E);
        var f = new MeshFactory();
        f.AddBox(new Point3D(x, -w / 2 - 0.3, _zTop / 2 + 0.15), 0.06, 0.06, _zTop + 0.3);
        f.AddBox(new Point3D(x, w / 2 + 0.3, _zTop / 2 + 0.15), 0.06, 0.06, _zTop + 0.3);
        var post = MeshFactory.Model(f.ToMesh(), postColor, out var pb);
        _group.Children.Add(post);
        Register(post, pc, pb, postColor);

        double beamZ = pc.Direction == DetectionDirection.Vertical ? _zTop + 0.6 : _zTop + 0.15;
        var beamColor = pc.IsEnabled ? Color.FromRgb(0x7D, 0xCE, 0xA0) : Color.FromRgb(0xAA, 0xAA, 0xAA);
        SolidColorBrush bb;
        GeometryModel3D beam;
        if (pc.Direction == DetectionDirection.Vertical)
            beam = MeshFactory.Box(new Point3D(x, 0, _zTop + 0.35), 0.02, 0.02, 0.7, beamColor, out bb);
        else
            beam = MeshFactory.Box(new Point3D(x, 0, beamZ), 0.02, w + 0.6, 0.02, beamColor, out bb);
        _group.Children.Add(beam);
        Register(beam, pc, bb, beamColor);
        _beamBrushes[pc.Id] = bb;
    }

    /// <summary>
    /// Legatrice KNCA (modello semplificato): basamento con armadio laterale, montante e arco di legatura
    /// che avvolge la linea. Le quote derivano dalla configurazione (ingombro in linea, altezza).
    /// </summary>
    private void AddStrapper(StrapperConfig st)
    {
        double x = st.PositionMm / 1000.0, w = _lineWidth, fp = Math.Max(0.3, st.FootprintLengthMm / 1000.0);
        double h = _zTop + 1.3;
        var color = st.IsEnabled ? GreyBody : GreyOff;
        double side = st.Side == LateralSide.Left ? 1 : -1;
        var f = new MeshFactory();
        f.AddBox(new Point3D(x, side * (w / 2 + 0.45), 0.45), fp, 0.5, 0.9);                          // basamento / armadio
        f.AddBox(new Point3D(x, side * (w / 2 + 0.3), h / 2), Math.Min(fp, 0.35), 0.2, h);             // montante
        f.AddBox(new Point3D(x, -side * (w / 2 + 0.2), h / 2), 0.2, 0.2, h);                           // montante opposto
        f.AddBox(new Point3D(x, 0, h - 0.1), 0.25, w + 0.9, 0.2);                                      // arco di legatura
        f.AddBox(new Point3D(x, 0, _zTop + 0.02), 0.03, w + 0.3, 0.03);                                // piano di legatura
        var m = MeshFactory.Model(f.ToMesh(), color, out var b);
        _group.Children.Add(m);
        Register(m, st, b, color);
        _strapperBrushes[st.Id] = b;
    }

    private void AddStopper(StopperConfig s)
    {
        double x = s.PositionMm / 1000.0, h = Math.Max(0.05, s.GraphicHeightMm / 1000.0);
        var color = Color.FromRgb(0xC0, 0x39, 0x2B);
        var m = MeshFactory.Box(new Point3D(x + 0.06, 0, _zTop - 0.1 + h / 2 + 0.05), 0.12, _lineWidth * 0.9, h + 0.1, color, out var b);
        _group.Children.Add(m);
        Register(m, s, b, color);
        _stopperBrush = b;
    }

    private void AddTransfer(TransferConfig t)
    {
        double x0 = t.PositionMm / 1000.0, len = t.LengthMm / 1000.0, stroke = t.TranslationStrokeMm / 1000.0;
        double sign = t.DestinationSide == LateralSide.Left ? 1 : -1;
        var color = Color.FromRgb(0xF1, 0xC4, 0x0F);
        var f = new MeshFactory();
        int forks = Math.Max(2, (int)Math.Round(len / 2.5) + 1);
        for (int i = 0; i < forks; i++)
        {
            double x = x0 + 0.2 + (len - 0.4) * i / (forks - 1);
            f.AddBox(new Point3D(x, 0, _zTop - 0.06), 0.12, _lineWidth + 0.2, 0.08);
        }
        var m = MeshFactory.Model(f.ToMesh(), color, out var b);
        _transferT = new TranslateTransform3D();
        m.Transform = _transferT;
        _group.Children.Add(m);
        Register(m, t, b, color);
        _transferBrush = b;

        // Guide di traslazione e stazione di destinazione
        var rails = new MeshFactory();
        for (int i = 0; i < forks; i++)
        {
            double x = x0 + 0.2 + (len - 0.4) * i / (forks - 1);
            rails.AddBox(new Point3D(x, sign * stroke / 2, _zTop - 0.25), 0.1, stroke + _lineWidth, 0.06);
        }
        rails.AddBox(new Point3D(x0 + len / 2, sign * stroke, _zTop - 0.12), len, _lineWidth * 0.8, 0.04);
        var railColor = Color.FromRgb(0x7F, 0x8C, 0x8D);
        var rm = MeshFactory.Model(rails.ToMesh(), railColor, out var rb);
        _group.Children.Add(rm);
        Register(rm, t, rb, railColor);
    }

    /// <summary>Stringifascio KNOF: portale con traversa di chiusura mobile (abbassata = chiuso).</summary>
    private void AddBundleClamp(BundleClampConfig k)
    {
        double x = k.PositionMm / 1000.0, w = _lineWidth;
        var frameColor = GreyBody;
        var f = new MeshFactory();
        f.AddBox(new Point3D(x, w / 2 + 0.25, (_zTop + 1.4) / 2), 0.25, 0.2, _zTop + 1.4);
        f.AddBox(new Point3D(x, -w / 2 - 0.25, (_zTop + 1.4) / 2), 0.25, 0.2, _zTop + 1.4);
        f.AddBox(new Point3D(x, 0, _zTop + 1.45), 0.3, w + 0.7, 0.15);
        var fm = MeshFactory.Model(f.ToMesh(), frameColor, out var fb);
        _group.Children.Add(fm);
        Register(fm, k, fb, frameColor);
        var barColor = GreyMoving;
        var bar = MeshFactory.Box(new Point3D(x, 0, _zTop + 1.2), 0.2, w + 0.2, 0.12, barColor, out var bb);
        bar.Transform = _knofT;
        _group.Children.Add(bar);
        Register(bar, k, bb, barColor);
        _knofBrush = bb;
    }

    /// <summary>Compattatore laterale: due ganasce che si chiudono verso l'asse linea.</summary>
    private void AddCompactor(SideCompactorConfig c)
    {
        double x = c.PositionMm / 1000.0, w = _lineWidth, len = Math.Max(0.3, c.JawLengthMm / 1000.0);
        var color = Color.FromRgb(0x7D, 0x3C, 0x98);
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

    /// <summary>Fermo intermedio: blocco che si solleva sopra il piano rulli quando armato.</summary>
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
        double y = sign * t.TranslationStrokeMm / 1000.0;
        var color = Color.FromRgb(0x17, 0xA5, 0x89);
        var f = new MeshFactory();
        f.AddBox(new Point3D(x0 + len / 2, y, _zTop - 0.08), len - 0.05, _lineWidth * 0.9, 0.06);
        f.AddBox(new Point3D(x0 + len / 2, y, (_zTop - 0.1) / 2), len * 0.8, _lineWidth * 0.5, _zTop - 0.1);
        var m = MeshFactory.Model(f.ToMesh(), color, out var b);
        _group.Children.Add(m);
        Register(m, sc, b, color);
        _scaleVisuals.Add((b, sc));
    }

    private (MeshGeometry3D Plain, MeshGeometry3D Strapped) PackageMeshes(PackageSnapshot ps)
    {
        if (_packageMeshes.TryGetValue(ps.TypeId, out var m)) return m;
        double L = ps.LengthM, W = ps.GraphicWidthMm / 1000.0, H = ps.GraphicHeightMm / 1000.0;
        var plain = new MeshFactory();
        plain.AddBox(new Point3D(0, 0, 0), L, W, H);
        var strapped = new MeshFactory();
        strapped.AddBox(new Point3D(0, 0, 0), L, W, H);
        var type = _project?.PackageTypes.FirstOrDefault(t => t.Id == ps.TypeId);
        if (type != null)
            foreach (var d in type.StrapPositionsMm)
                strapped.AddBox(new Point3D(L / 2 - d / 1000.0, 0, 0), 0.035, W + 0.02, H + 0.02);
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
                    var model = MeshFactory.Model(strapped ? meshes.Strapped : meshes.Plain, Color.FromRgb(0x85, 0x92, 0x9E), out _);
                    var t = new TranslateTransform3D();
                    model.Transform = t;
                    _packagesGroup.Children.Add(model);
                    entry = (model, t, strapped);
                    _packages[ps.Id] = entry;
                }
                if (entry.Strapped != strapped)
                {
                    entry.Model.Geometry = strapped ? meshes.Strapped : meshes.Plain;
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
            b.Color = !st.IsEnabled ? GreyOff : state == StrapperState.Idle ? GreyBody : Blend(GreyBody, GreyMoving, 0.6);
        }

        // Zone: struttura accesa se l'inverter è in moto
        foreach (var (inv, brush, color) in _sectionFrameBrushes)
        {
            int zi = _zoneOrder.IndexOf(inv);
            bool moving = f != null && zi >= 0 && zi < f.ZoneSpeeds.Length && f.ZoneSpeeds[zi] > 1e-4;
            if (_highlighted is RollerSection rs && rs.Id == inv) continue;
            brush.Color = moving ? color : Blend(color, Color.FromRgb(0x55, 0x55, 0x55), 0.6);
        }

        // Trasferitore
        if (f != null)
        {
            _transferT.OffsetY = sign * f.TransferLateralM;
            _transferT.OffsetZ = f.TransferLiftM;
        }
        else { _transferT.OffsetY = 0; _transferT.OffsetZ = 0; }
        if (_stopperBrush != null && !IsHighlighted(p.Stopper))
            _stopperBrush.Color = f != null && f.StopperState != StopperState.Free ? Color.FromRgb(0xFF, 0x6F, 0x00) : Color.FromRgb(0xC0, 0x39, 0x2B);
        if (_transferBrush != null && !IsHighlighted(p.Transfer))
            _transferBrush.Color = f != null && f.TransferState != TransferState.Home ? Color.FromRgb(0xF3, 0x9C, 0x12) : Color.FromRgb(0xF1, 0xC4, 0x0F);

        // Legatrici KNCA in guasto: rosso
        for (int i = 0; i < p.Strappers.Count; i++)
        {
            if (f == null || i >= f.StrapperProcessStates.Length || f.StrapperProcessStates[i] != StrapperProcessState.Fault) continue;
            if (_strapperBrushes.TryGetValue(p.Strappers[i].Id, out var b) && !IsHighlighted(p.Strappers[i])) b.Color = FaultRed;
        }

        // Stringifascio: traversa che scende in chiusura
        _knofT.OffsetZ = -(f?.BundleClampClosed ?? 0) * 0.6;
        if (_knofBrush != null && !IsHighlighted(p.BundleClamp))
            _knofBrush.Color = (f?.BundleClampState ?? ClampState.Open) switch { ClampState.Fault => FaultRed, ClampState.Closed => Blend(GreyMoving, GreyBody, 0.5), _ => GreyMoving };

        // Compattatore: ganasce verso l'asse
        double jaw = (f?.CompactorClosed ?? 0) * 0.12;
        _jawLeftT.OffsetY = -jaw;
        _jawRightT.OffsetY = jaw;
        if (_compactorBrush != null && !IsHighlighted(p.Compactor))
        {
            var cc = ClampColor(f?.CompactorState ?? ClampState.Open, Color.FromRgb(0x7D, 0x3C, 0x98));
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

        // Fermi intermedi: sollevati e colorati secondo lo stato
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
                StopState.Aligning => Color.FromRgb(0xF3, 0x9C, 0x12),
                StopState.Aligned => Color.FromRgb(0xF7, 0xDC, 0x6F),
                StopState.Positioning or StopState.Assigned => Color.FromRgb(0xE7, 0x4C, 0x3C),
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
                ScaleState.Idle => Color.FromRgb(0x17, 0xA5, 0x89),
                ScaleState.Stabilizing or ScaleState.Acquiring => Color.FromRgb(0xF4, 0xD0, 0x3F),
                ScaleState.Completed or ScaleState.Releasing => Color.FromRgb(0x28, 0xB4, 0x63),
                ScaleState.Fault => Color.FromRgb(0xC0, 0x39, 0x2B),
                _ => Color.FromRgb(0x5D, 0xAD, 0xE2)
            };
        }
    }

    private static Color ClampColor(ClampState s, Color idle) => s switch
    {
        ClampState.Closing or ClampState.Opening => Color.FromRgb(0xF3, 0x9C, 0x12),
        ClampState.Closed => Color.FromRgb(0xE6, 0x7E, 0x22),
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
