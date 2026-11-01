using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Tipo funzionale di una zona inverter.</summary>
public enum ZoneKind
{
    /// <summary>Trasporto/accumulo: può contenere più pacchi.</summary>
    Transport = 0,
    /// <summary>Zona della stazione di legatura: un pacco alla volta, posizionamento.</summary>
    Strapping = 1,
    /// <summary>Zona che contiene lo stopper: un pacco alla volta, allineamento.</summary>
    Stopper = 2
}

/// <summary>Zona = insieme delle sezioni comandate dallo stesso inverter (unità di comando).</summary>
public sealed class ZoneInfo
{
    public required InverterConfig Inverter { get; init; }
    public required IReadOnlyList<RollerSection> Sections { get; init; }
    public int Index { get; internal set; }
    public double StartM { get; init; }
    public double EndM { get; init; }
    public bool IsContiguous { get; init; }
    public ZoneKind Kind { get; internal set; }
    public double LengthM => EndM - StartM;
    public string Id => Inverter.Id;
}

/// <summary>
/// Topologia monodimensionale della linea: asse x lungo la linea, verso positivo = flusso.
/// Le zone sono ordinate da monte a valle in base all'inizio della prima sezione.
/// </summary>
public sealed class LineTopology
{
    private readonly Dictionary<string, ZoneInfo> _byInverter;

    public LineTopology(LineProject project)
    {
        Project = project;
        var zones = new List<ZoneInfo>();
        foreach (var inv in project.Inverters)
        {
            var secs = project.Sections.Where(s => s.InverterId == inv.Id).OrderBy(s => s.StartMm).ToList();
            if (secs.Count == 0) continue;
            bool contiguous = true;
            for (int i = 1; i < secs.Count; i++)
                if (Math.Abs(secs[i].StartMm - secs[i - 1].EndMm) > 0.5) contiguous = false;
            zones.Add(new ZoneInfo
            {
                Inverter = inv,
                Sections = secs,
                StartM = Units.MmToM(secs.Min(s => s.StartMm)),
                EndM = Units.MmToM(secs.Max(s => s.EndMm)),
                IsContiguous = contiguous
            });
        }
        zones.Sort((a, b) => a.StartM.CompareTo(b.StartM));
        for (int i = 0; i < zones.Count; i++) zones[i].Index = i;

        Zones = zones;
        _byInverter = new Dictionary<string, ZoneInfo>();
        foreach (var z in zones) _byInverter.TryAdd(z.Inverter.Id, z); // ID duplicati segnalati dal validatore

        LineStartM = project.Sections.Count > 0 ? Units.MmToM(project.Sections.Min(s => s.StartMm)) : 0;
        LineEndM = project.Sections.Count > 0 ? Units.MmToM(project.Sections.Max(s => s.EndMm)) : 0;

        // Classificazione zone
        StopperZone = ZoneAt(Units.MmToM(project.Stopper.PositionMm) - 1e-6);
        if (StopperZone != null) StopperZone.Kind = ZoneKind.Stopper;
        if (project.StrappingStation.IsEnabled && _byInverter.TryGetValue(project.StrappingStation.InverterId ?? string.Empty, out var sz)
            && sz != StopperZone)
        {
            StrappingZone = sz;
            sz.Kind = ZoneKind.Strapping;
        }
    }

    public LineProject Project { get; }
    public IReadOnlyList<ZoneInfo> Zones { get; }
    public double LineStartM { get; }
    public double LineEndM { get; }
    public ZoneInfo? StopperZone { get; }
    public ZoneInfo? StrappingZone { get; }

    public ZoneInfo? Zone(string inverterId) => _byInverter.TryGetValue(inverterId, out var z) ? z : null;

    /// <summary>Zona che contiene la posizione x (in m), o null se fuori dalle sezioni.</summary>
    public ZoneInfo? ZoneAt(double xM)
    {
        foreach (var z in Zones)
            foreach (var s in z.Sections)
                if (xM >= Units.MmToM(s.StartMm) && xM < Units.MmToM(s.EndMm)) return z;
        return null;
    }

    public ZoneInfo? Downstream(ZoneInfo zone) => zone.Index + 1 < Zones.Count ? Zones[zone.Index + 1] : null;
    public ZoneInfo? Upstream(ZoneInfo zone) => zone.Index > 0 ? Zones[zone.Index - 1] : null;
}
