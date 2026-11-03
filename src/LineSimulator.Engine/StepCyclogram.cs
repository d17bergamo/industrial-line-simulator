namespace LineSimulator.Engine;

/// <summary>Livello del gradino: 0 = fermo / non attivo, 1 = velocità bassa o fase di transizione, 2 = attivo / velocità alta.</summary>
public enum StepLevel { Inactive = 0, Intermediate = 1, Active = 2 }

/// <summary>Cambio di livello di una riga: dal tempo T (già arrotondato) in poi la riga resta a Level.</summary>
public readonly record struct StepPoint(double TimeS, StepLevel Level);

public sealed record StepRow(string Key, string Label, TrackGroup Group, IReadOnlyList<StepPoint> Points, double EndS);

/// <summary>
/// Ciclogramma "a gradini". L'arrotondamento a 0,5 s per eccesso vale SOLO qui: il motore e i risultati
/// della simulazione restano ai tempi esatti.
/// </summary>
public static class StepCyclogram
{
    public const double RoundingS = 0.5;

    /// <summary>Arrotonda per eccesso al mezzo secondo (es. 1,01 → 1,5; 2,0 → 2,0).</summary>
    public static double RoundUp(double t)
        => Math.Ceiling(Math.Round(t / RoundingS, 9)) * RoundingS;

    public static StepLevel LevelOf(CycleState s) => s switch
    {
        CycleState.On or CycleState.High or CycleState.Accelerating or CycleState.Working or CycleState.Positioning
            or CycleState.Lifting or CycleState.Translating or CycleState.Closing or CycleState.Ejecting
            or CycleState.Unloading or CycleState.Depositing or CycleState.Returning or CycleState.Opening
            or CycleState.Raised or CycleState.Aligning or CycleState.Preparing or CycleState.Acquiring
            or CycleState.Stabilizing or CycleState.Lowering => StepLevel.Active,
        CycleState.Low or CycleState.Decelerating or CycleState.Consent or CycleState.InPosition
            or CycleState.Aligned or CycleState.Closed => StepLevel.Intermediate,
        _ => StepLevel.Inactive
    };

    public static IReadOnlyList<StepRow> Build(SimulationRecorder recorder, IReadOnlySet<TrackGroup>? groups = null)
    {
        var rows = new List<StepRow>();
        foreach (var track in recorder.Tracks)
        {
            if (groups != null && !groups.Contains(track.Group)) continue;
            if (track.Segments.Count == 0) continue;
            var pts = new List<StepPoint>();
            foreach (var seg in track.Segments)
            {
                var level = LevelOf(seg.State);
                double t = RoundUp(seg.StartS);
                if (pts.Count > 0 && pts[^1].Level == level) continue;
                // un segmento più breve del passo viene assorbito: due cambi allo stesso istante contano l'ultimo
                if (pts.Count > 0 && pts[^1].TimeS >= t) { pts[^1] = new StepPoint(pts[^1].TimeS, level); if (pts.Count > 1 && pts[^2].Level == level) pts.RemoveAt(pts.Count - 1); continue; }
                pts.Add(new StepPoint(t, level));
            }
            rows.Add(new StepRow(track.Key, track.Label, track.Group, pts, RoundUp(track.Segments[^1].EndS)));
        }
        return rows;
    }

    /// <summary>Durata totale (arrotondata) del ciclogramma.</summary>
    public static double TotalS(IReadOnlyList<StepRow> rows) => rows.Count == 0 ? 0 : rows.Max(r => r.EndS);
}
