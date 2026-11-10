using HelixToolkit.Wpf;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LineSimulator.Models;

namespace LineSimulator.App.Rendering;

/// <summary>Anteprima 3D di una tipologia di pacco: parallelepipedo con le legature nelle posizioni impostate (dalla testa, a destra).</summary>
public sealed class PackageScene
{
    private static readonly Color Wood = Color.FromRgb(0xD3, 0xDB, 0xE5);
    private static readonly Color Strap = Color.FromRgb(0x3C, 0x48, 0x56);
    private static readonly Color StrapBad = Color.FromRgb(0xC0, 0x39, 0x2B);

    public ModelVisual3D Visual { get; } = new();
    public Rect3D Bounds { get; private set; } = Rect3D.Empty;
    private static readonly Color StrapSel = Color.FromRgb(0xFF, 0x8C, 0x00);
    private readonly List<Model3D> _straps = new();
    /// <summary>Indice (ordinato dalla testa) della legatura selezionata, -1 nessuna.</summary>
    public int SelectedIndex { get; set; } = -1;
    /// <summary>Altezza del pacco in metri (piano su cui si trascinano le legature).</summary>
    public double HeightM { get; private set; }
    public double LengthM { get; private set; }

    /// <summary>Indice della legatura a cui appartiene il modello colpito, -1 se nessuna.</summary>
    public int PickStrap(Model3D? hit) => hit == null ? -1 : _straps.IndexOf(hit);

    /// <summary>Quote tra le legature (e dalle estremità) sopra il pacco: attive di default, si tolgono con la spunta.</summary>
    public bool ShowDimensions { get; set; } = true;
    private static readonly Color DimColor = Color.FromRgb(0x0B, 0x5F, 0xC0);

    public void Build(PackageType? p)
    {
        var g = new Model3DGroup();
        Visual.Content = g;
        Visual.Children.Clear();
        _straps.Clear();
        if (p == null) { Bounds = Rect3D.Empty; return; }
        double L = Math.Max(0.1, p.LengthMm / 1000.0), W = Math.Max(0.05, p.GraphicWidthMm / 1000.0), H = Math.Max(0.05, p.GraphicHeightMm / 1000.0);
        g.Children.Add(MeshFactory.Box(new Point3D(0, 0, H / 2), L, W, H, Wood, out _));
        var floor = MeshFactory.Box(new Point3D(0, 0, -0.01), L + 1.0, W + 1.0, 0.02, Color.FromRgb(0xE8, 0xEB, 0xEE), out _);
        g.Children.Add(floor);

        double minEnd = p.MinStrapDistanceFromEndsMm / 1000.0, minGap = p.MinDistanceBetweenStrapsMm / 1000.0;
        HeightM = H; LengthM = L;
        var pos = p.StrapPositionsMm.OrderBy(v => v).Select(v => v / 1000.0).ToList();
        for (int i = 0; i < pos.Count; i++)
        {
            double s = pos[i];
            bool bad = s < minEnd || s > L - minEnd || pos.Where((_, j) => j != i).Any(o => Math.Abs(o - s) < minGap);
            double x = L / 2 - s, t = 0.06, e = 0.02;
            var f = new MeshFactory();
            f.AddBox(new Point3D(x, 0, H + e / 2), t, W + 2 * e, e);       // sopra
            f.AddBox(new Point3D(x, 0, -e / 2 + 0.0), t, W + 2 * e, e);    // sotto
            f.AddBox(new Point3D(x, W / 2 + e / 2, H / 2), t, e, H);       // fianchi
            f.AddBox(new Point3D(x, -W / 2 - e / 2, H / 2), t, e, H);
            var sm = MeshFactory.Model(f.ToMesh(), i == SelectedIndex ? StrapSel : bad ? StrapBad : Strap, out _);
            g.Children.Add(sm);
            _straps.Add(sm);
        }
        if (ShowDimensions && pos.Count > 0) AddDimensions(g, pos, L, W, H);
        Bounds = new Rect3D(-L / 2 - 0.5, -W / 2 - 0.5, 0, L + 1.0, W + 1.0, H);
    }

    /// <summary>Catena di quote lungo il fianco del pacco: testa → prima legatura → … → ultima → coda, in mm.</summary>
    private void AddDimensions(Model3DGroup g, List<double> pos, double L, double W, double H)
    {
        double y = -W / 2 - 0.28, z = H + 0.05;
        var marks = new List<double> { 0 };
        marks.AddRange(pos);
        marks.Add(L);
        var f = new MeshFactory();
        f.AddBox(new Point3D(0, y, z), L, 0.012, 0.012);                                   // linea di quota
        foreach (var m in marks)
        {
            double x = L / 2 - m;
            f.AddBox(new Point3D(x, y, z), 0.012, 0.012, 0.16);                            // segno
            f.AddBox(new Point3D(x, (y - W / 2) / 2 + 0.07, H + 0.005), 0.008, Math.Abs(y + W / 2) - 0.02, 0.006);   // richiamo verso il pacco
        }
        g.Children.Add(MeshFactory.Model(f.ToMesh(), DimColor, out _));
        for (int i = 1; i < marks.Count; i++)
        {
            double a = marks[i - 1], b = marks[i];
            if (b - a < 1e-6) continue;
            Visual.Children.Add(new BillboardTextVisual3D
            {
                Text = $"{(b - a) * 1000:0}",
                Position = new Point3D(L / 2 - (a + b) / 2, y, z + 0.14),
                FontSize = 12,
                Foreground = new SolidColorBrush(DimColor),
                Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
                Padding = new System.Windows.Thickness(3, 1, 3, 1)
            });
        }
    }
}
