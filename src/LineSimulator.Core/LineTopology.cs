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

/// <summary>Zona di comando: uno o più moduli di via a rulli contigui comandati dallo stesso inverter.</summary>
public sealed class ZoneInfo
{
    /// <summary>Parametri di azionamento del modulo.</summary>
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
        // Zona di comando = insieme dei moduli con la stessa zona/inverter (default: un inverter per modulo).
        // I parametri di azionamento sono quelli del primo modulo della zona.
        var zones = new List<ZoneInfo>();
        foreach (var g in project.Sections.GroupBy(s => s.EffectiveZoneId))
        {
            var secs = g.OrderBy(s => s.StartMm).ToList();
            bool contiguous = true;
            for (int i = 1; i < secs.Count; i++)
                if (Math.Abs(secs[i].StartMm - secs[i - 1].EndMm) > 0.5) contiguous = false;
            var drive = secs[0].ToDrive();
            drive.Id = g.Key;
            if (secs.Count > 1 || g.Key != secs[0].Id)
                drive.Name = secs.Count > 1 ? $"Inverter {g.Key} ({string.Join("+", secs.Select(x => x.Id))})" : $"Inverter {g.Key}";
            drive.IsEnabled = secs.All(x => x.IsEnabled);
            zones.Add(new ZoneInfo
            {
                Inverter = drive,
                Sections = secs,
                StartM = Units.MmToM(secs[0].StartMm),
                EndM = Units.MmToM(secs.Max(x => x.EndMm)),
                IsContiguous = contiguous
            });
        }
        zones.Sort((a, b) => a.StartM.CompareTo(b.StartM));
        for (int i = 0; i < zones.Count; i++) zones[i].Index = i;

        Zones = zones;
        _byInverter = new Dictionary<string, ZoneInfo>();
        foreach (var z in zones) _byInverter.TryAdd(z.Inverter.Id, z); // ID duplicati segnalati dal validatore
        // Alias: un riferimento all'ID di un modulo indica la sua zona di comando
        foreach (var z in zones)
            foreach (var sec in z.Sections) _byInverter.TryAdd(sec.Id, z);

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

    /// <summary>Zona di comando per ID di zona/inverter o per ID di un suo modulo.</summary>
    public ZoneInfo? Zone(string inverterId) => _byInverter.TryGetValue(inverterId ?? string.Empty, out var z) ? z : null;

    /// <summary>ID della zona di comando a cui si riferisce un ID (zona o modulo); invariato se sconosciuto.</summary>
    public string ResolveZoneId(string id) => Zone(id)?.Id ?? id;

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
