using System.Windows.Media;
using System.Windows.Media.Media3D;
using LineSimulator.Models;

namespace LineSimulator.App.Rendering;

/// <summary>Anteprima 3D di una tipologia di pacco: parallelepipedo con le legature nelle posizioni impostate (dalla testa, a destra).</summary>
public sealed class PackageScene
{
    private static readonly Color Wood = Color.FromRgb(0xD9, 0xC2, 0x9A);
    private static readonly Color Strap = Color.FromRgb(0x2E, 0x9E, 0x50);
    private static readonly Color StrapBad = Color.FromRgb(0xC0, 0x39, 0x2B);

    public ModelVisual3D Visual { get; } = new();
    public Rect3D Bounds { get; private set; } = Rect3D.Empty;

    public void Build(PackageType? p)
    {
        var g = new Model3DGroup();
        Visual.Content = g;
        if (p == null) { Bounds = Rect3D.Empty; return; }
        double L = Math.Max(0.1, p.LengthMm / 1000.0), W = Math.Max(0.05, p.GraphicWidthMm / 1000.0), H = Math.Max(0.05, p.GraphicHeightMm / 1000.0);
        g.Children.Add(MeshFactory.Box(new Point3D(0, 0, H / 2), L, W, H, Wood, out _));
        var floor = MeshFactory.Box(new Point3D(0, 0, -0.01), L + 1.0, W + 1.0, 0.02, Color.FromRgb(0xE8, 0xEB, 0xEE), out _);
        g.Children.Add(floor);

        double minEnd = p.MinStrapDistanceFromEndsMm / 1000.0, minGap = p.MinDistanceBetweenStrapsMm / 1000.0;
        var pos = p.StrapPositionsMm.Select(v => v / 1000.0).ToList();
        for (int i = 0; i < pos.Count; i++)
        {
            double s = pos[i];
            bool bad = s < minEnd || s > L - minEnd || pos.Where((_, j) => j != i).Any(o => Math.Abs(o - s) < minGap);
            double x = L / 2 - s, t = 0.04, e = 0.012;
            var f = new MeshFactory();
            f.AddBox(new Point3D(x, 0, H + e / 2), t, W + 2 * e, e);       // sopra
            f.AddBox(new Point3D(x, 0, -e / 2 + 0.0), t, W + 2 * e, e);    // sotto
            f.AddBox(new Point3D(x, W / 2 + e / 2, H / 2), t, e, H);       // fianchi
            f.AddBox(new Point3D(x, -W / 2 - e / 2, H / 2), t, e, H);
            g.Children.Add(MeshFactory.Model(f.ToMesh(), bad ? StrapBad : Strap, out _));
        }
        Bounds = new Rect3D(-L / 2 - 0.5, -W / 2 - 0.5, 0, L + 1.0, W + 1.0, H);
    }
}
