using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LineSimulator.App.Rendering;

/// <summary>Costruzione di mesh semplici (parallelepipedi e cilindri) senza dipendenze esterne.</summary>
public sealed class MeshFactory
{
    private readonly Point3DCollection _pos = new();
    private readonly Vector3DCollection _nrm = new();
    private readonly Int32Collection _idx = new();

    public void AddBox(Point3D c, double sx, double sy, double sz)
    {
        double x0 = c.X - sx / 2, x1 = c.X + sx / 2, y0 = c.Y - sy / 2, y1 = c.Y + sy / 2, z0 = c.Z - sz / 2, z1 = c.Z + sz / 2;
        Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), new Vector3D(0, 0, 1));   // sopra
        Quad(new(x0, y1, z0), new(x1, y1, z0), new(x1, y0, z0), new(x0, y0, z0), new Vector3D(0, 0, -1));  // sotto
        Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), new Vector3D(0, -1, 0));  // fronte
        Quad(new(x1, y1, z0), new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new Vector3D(0, 1, 0));   // retro
        Quad(new(x0, y1, z0), new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new Vector3D(-1, 0, 0));  // sinistra
        Quad(new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1), new Vector3D(1, 0, 0));   // destra
    }

    /// <summary>Cilindro con asse parallelo a Y (rullo trasversale).</summary>
    public void AddCylinderY(double x, double z, double y0, double y1, double radius, int segments = 12)
    {
        int baseIdx = _pos.Count;
        for (int i = 0; i <= segments; i++)
        {
            double a = 2 * Math.PI * i / segments;
            double cx = Math.Cos(a), cz = Math.Sin(a);
            _pos.Add(new Point3D(x + radius * cx, y0, z + radius * cz));
            _pos.Add(new Point3D(x + radius * cx, y1, z + radius * cz));
            _nrm.Add(new Vector3D(cx, 0, cz));
            _nrm.Add(new Vector3D(cx, 0, cz));
        }
        for (int i = 0; i < segments; i++)
        {
            int a = baseIdx + 2 * i, b = a + 1, c = a + 2, d = a + 3;
            _idx.Add(a); _idx.Add(c); _idx.Add(b);
            _idx.Add(b); _idx.Add(c); _idx.Add(d);
        }
    }

    private void Quad(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D n)
    {
        int i = _pos.Count;
        _pos.Add(a); _pos.Add(b); _pos.Add(c); _pos.Add(d);
        for (int k = 0; k < 4; k++) _nrm.Add(n);
        _idx.Add(i); _idx.Add(i + 1); _idx.Add(i + 2);
        _idx.Add(i); _idx.Add(i + 2); _idx.Add(i + 3);
    }

    public MeshGeometry3D ToMesh()
    {
        var m = new MeshGeometry3D { Positions = _pos, Normals = _nrm, TriangleIndices = _idx };
        m.Freeze();
        return m;
    }

    public static GeometryModel3D Model(MeshGeometry3D mesh, Color color, out SolidColorBrush brush)
    {
        brush = new SolidColorBrush(color);
        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(brush));
        mat.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 20));
        return new GeometryModel3D(mesh, mat) { BackMaterial = new DiffuseMaterial(brush) };
    }

    public static GeometryModel3D Box(Point3D c, double sx, double sy, double sz, Color color, out SolidColorBrush brush)
    {
        var f = new MeshFactory();
        f.AddBox(c, sx, sy, sz);
        return Model(f.ToMesh(), color, out brush);
    }
}
