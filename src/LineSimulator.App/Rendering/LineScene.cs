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

        for (int i = 0; i < p.Inverters.Count; i++)
        {
            var c = Palette[i % Palette.Length];
            _zoneBrushes[p.Inverters[i].Id] = (new SolidColorBrush(c), c);
        }

        foreach (var s in p.Sections) AddSection(s);
        foreach (var pc in p.Photocells) AddPhotocell(pc);
        foreach (var st in p.Strappers) AddStrapper(st);
        AddStopper(p.Stopper);
        AddTransfer(p.Transfer);

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
        var zc = _zoneBrushes.TryGetValue(s.InverterId, out var zb) ? zb : (new SolidColorBrush(Colors.Gray), Colors.Gray);

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
        if (_zoneBrushes.ContainsKey(s.InverterId))
        {
            // il colore della struttura indica lo stato della zona (vedi ApplyFrame)
            _sectionFrameBrushes.Add((s.InverterId, fb, zc.Item2));
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

    private void AddStrapper(StrapperConfig st)
    {
        double x = st.PositionMm / 1000.0, w = _lineWidth, fp = Math.Max(0.3, st.FootprintLengthMm / 1000.0);
        var color = st.IsEnabled ? Color.FromRgb(0x5D, 0x6D, 0x7E) : Color.FromRgb(0xBB, 0xBB, 0xBB);
        var f = new MeshFactory();
        double side = st.Side == LateralSide.Left ? 1 : -1;
        f.AddBox(new Point3D(x, side * (w / 2 + 0.45), (_zTop + 1.3) / 2), fp, 0.5, _zTop + 1.3);   // corpo macchina
        f.AddBox(new Point3D(x, -side * (w / 2 + 0.2), (_zTop + 1.3) / 2), 0.2, 0.2, _zTop + 1.3);   // montante opposto
        f.AddBox(new Point3D(x, 0, _zTop + 1.2), 0.25, w + 0.9, 0.2);                                // arco
        f.AddBox(new Point3D(x, 0, _zTop + 0.02), 0.03, w + 0.3, 0.03);                             // piano di legatura
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
            b.Color = state switch
            {
                StrapperState.Preparing => Color.FromRgb(0xF4, 0xD0, 0x3F),
                StrapperState.Strapping => Color.FromRgb(0xE6, 0x7E, 0x22),
                StrapperState.Returning => Color.FromRgb(0x5D, 0xAD, 0xE2),
                _ => st.IsEnabled ? Color.FromRgb(0x5D, 0x6D, 0x7E) : Color.FromRgb(0xBB, 0xBB, 0xBB)
            };
        }

        // Zone: struttura accesa se l'inverter è in moto
        foreach (var (inv, brush, color) in _sectionFrameBrushes)
        {
            int zi = _zoneOrder.IndexOf(inv);
            bool moving = f != null && zi >= 0 && zi < f.ZoneSpeeds.Length && f.ZoneSpeeds[zi] > 1e-4;
            if (_highlighted is RollerSection rs && rs.InverterId == inv) continue;
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
    }

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
