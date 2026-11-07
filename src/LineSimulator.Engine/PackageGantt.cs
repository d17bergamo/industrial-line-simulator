using LineSimulator.Engine.Runtime;

namespace LineSimulator.Engine;

/// <summary>Tratto del Gantt di un pacco: un movimento (con distanza, velocità e accelerazione) o una sosta in uno stato.</summary>
public sealed class GanttSegment
{
    public double StartS { get; init; }
    public double EndS { get; internal set; }
    public double DurationS => EndS - StartS;
    /// <summary>Vero se il pacco si muove sui rulli (altrimenti è fermo, sul trasferitore o fuori linea).</summary>
    public bool Moving { get; init; }
    public PackageState State { get; init; }
    /// <summary>Posizione della testa all'inizio e alla fine del tratto (m dall'inizio della linea).</summary>
    public double FromM { get; init; }
    public double ToM { get; internal set; }
    public double DistanceM => Math.Abs(ToM - FromM);
    public double MaxSpeed { get; internal set; }
    /// <summary>Velocità media nel tratto (distanza / durata).</summary>
    public double MeanSpeed => DurationS > 1e-9 ? DistanceM / DurationS : 0;
    /// <summary>Accelerazione massima e decelerazione massima (valori positivi, m/s²).</summary>
    public double MaxAcceleration { get; internal set; }
    public double MaxDeceleration { get; internal set; }
    public string Label => GanttLabels.State(State, Moving);
}

/// <summary>Riga del Gantt: un pacco dall'ingresso all'uscita.</summary>
public sealed class GanttRow
{
    public string PackageId { get; init; } = string.Empty;
    public string TypeId { get; init; } = string.Empty;
    public double LengthM { get; init; }
    public List<GanttSegment> Segments { get; } = new();
    public double StartS => Segments.Count > 0 ? Segments[0].StartS : 0;
    public double EndS => Segments.Count > 0 ? Segments[^1].EndS : 0;
    public double TotalDistanceM => Segments.Where(s => s.Moving).Sum(s => s.DistanceM);
    public double MaxSpeed => Segments.Count > 0 ? Segments.Max(s => s.MaxSpeed) : 0;
    public double MaxAcceleration => Segments.Count > 0 ? Segments.Max(s => s.MaxAcceleration) : 0;
    public double MaxDeceleration => Segments.Count > 0 ? Segments.Max(s => s.MaxDeceleration) : 0;
}

public static class GanttLabels
{
    public static string State(PackageState s, bool moving) => s switch
    {
        PackageState.Positioning => moving ? "Posizionamento" : "Fermo in posizionamento",
        PackageState.Strapping => "Legatura",
        PackageState.Aligning => moving ? "Accostamento al fermo" : "Intestatura",
        PackageState.Aligned => "Attesa trasferitore",
        PackageState.OnTransfer => "Sul trasferitore",
        PackageState.Accumulated => "Attesa (accumulo)",
        PackageState.Rejecting => "Espulsione",
        _ => moving ? "Trasporto" : "Fermo"
    };
}

/// <summary>
/// Gantt dell'avanzamento dei pacchi, ricavato dai fotogrammi della simulazione: per ogni pacco i tratti di movimento
/// (distanza, velocità massima e media, accelerazione e decelerazione massime) e le soste (legatura, attese, trasferitore).
/// </summary>
public static class PackageGantt
{
    /// <summary>Sotto questa velocità il pacco è considerato fermo (m/s).</summary>
    public const double StillSpeed = 0.005;

    public static List<GanttRow> Build(SimulationRecorder rec)
    {
        var rows = new Dictionary<string, GanttRow>();
        var last = new Dictionary<string, (double T, double V)>();
        var order = new List<string>();
        foreach (var f in rec.Frames)
        {
            foreach (var p in f.Packages)
            {
                if (!rows.TryGetValue(p.Id, out var row))
                {
                    row = new GanttRow { PackageId = p.Id, TypeId = p.TypeId, LengthM = p.LengthM };
                    rows[p.Id] = row;
                    order.Add(p.Id);
                }
                bool onRollers = p.State is not (PackageState.OnTransfer or PackageState.Exited or PackageState.Rejecting or PackageState.Rejected);
                bool moving = onRollers && Math.Abs(p.Velocity) > StillSpeed;
                var cur = row.Segments.Count > 0 ? row.Segments[^1] : null;
                if (cur == null || cur.Moving != moving || cur.State != p.State)
                {
                    // il tratto precedente arriva fino a questo fotogramma: tempi e posizioni restano contigui
                    if (cur != null) { cur.EndS = f.TimeS; if (cur.Moving) { cur.ToM = p.HeadM; cur.MaxSpeed = Math.Max(cur.MaxSpeed, Math.Abs(p.Velocity)); } }
                    cur = new GanttSegment { StartS = f.TimeS, EndS = f.TimeS, Moving = moving, State = p.State, FromM = p.HeadM, ToM = p.HeadM };
                    row.Segments.Add(cur);
                }
                cur.EndS = f.TimeS;
                cur.ToM = p.HeadM;
                if (onRollers) cur.MaxSpeed = Math.Max(cur.MaxSpeed, Math.Abs(p.Velocity));
                if (last.TryGetValue(p.Id, out var prev) && f.TimeS > prev.T + 1e-9 && onRollers)
                {
                    double a = (Math.Abs(p.Velocity) - Math.Abs(prev.V)) / (f.TimeS - prev.T);
                    if (a > 0) cur.MaxAcceleration = Math.Max(cur.MaxAcceleration, a);
                    else cur.MaxDeceleration = Math.Max(cur.MaxDeceleration, -a);
                }
                last[p.Id] = (f.TimeS, p.Velocity);
            }
        }
        // i pacchi usciti spariscono dai fotogrammi: l'ultimo tratto si chiude all'ultimo fotogramma in cui c'erano
        var result = order.Select(id => rows[id]).ToList();
        foreach (var r in result)
            r.Segments.RemoveAll(s => s.DurationS < 1e-9 && r.Segments.Count > 1);
        return result;
    }
}
