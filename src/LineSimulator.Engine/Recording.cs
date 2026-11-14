using LineSimulator.Engine.Runtime;

namespace LineSimulator.Engine;

/// <summary>Stati rappresentati nel ciclogramma.</summary>
public enum CycleState
{
    Off,
    On,
    Stopped,
    Accelerating,
    Decelerating,
    Low,
    High,
    Positioning,
    Aligning,
    Aligned,
    Idle,
    Preparing,
    Working,
    Returning,
    Lifting,
    Translating,
    Lowering,
    Depositing,
    Consent,
    Interlock,
    Waiting,
    InPosition,
    WaitConsent,
    Completed,
    Unloading,
    Fault,
    Closing,
    Closed,
    Opening,
    Ejecting,
    Stabilizing,
    Acquiring,
    Raised
}

public enum TrackGroup
{
    Inverter,
    Photocell,
    Strapper,
    Stopper,
    Transfer,
    Interlock,
    Clamp,
    Rejector,
    Stop,
    Scale
}

public sealed class CycleSegment
{
    public double StartS { get; init; }
    public double EndS { get; internal set; }
    public CycleState State { get; init; }
    public string Detail { get; init; } = string.Empty;
    public double DurationS => EndS - StartS;
}

/// <summary>Riga del ciclogramma (una utenza).</summary>
public sealed class CycleTrack
{
    public CycleTrack(string key, string label, TrackGroup group)
    {
        Key = key;
        Label = label;
        Group = group;
    }

    public string Key { get; }
    public string Label { get; }
    public TrackGroup Group { get; }
    public List<CycleSegment> Segments { get; } = new();

    internal void Set(double t, CycleState s, string detail)
    {
        if (Segments.Count > 0)
        {
            var last = Segments[^1];
            if (last.State == s && last.Detail == detail) { last.EndS = t; return; }
            last.EndS = t;
        }
        Segments.Add(new CycleSegment { StartS = t, EndS = t, State = s, Detail = detail });
    }

    internal void Close(double t)
    {
        if (Segments.Count > 0) Segments[^1].EndS = t;
    }

    public CycleSegment? At(double t)
    {
        int lo = 0, hi = Segments.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var s = Segments[mid];
            if (t < s.StartS) hi = mid - 1;
            else if (t > s.EndS) lo = mid + 1;
            else return s;
        }
        return null;
    }
}

public readonly record struct PackageSnapshot(
    string Id, string TypeId, double HeadM, double LengthM, double Velocity, PackageState State,
    double LiftM, double LateralM, int StrapsDone, int StrapsTotal, double GraphicWidthMm, double GraphicHeightMm);

/// <summary>Fotogramma dello stato completo in un istante: usato per la riproduzione e lo scorrimento della timeline.</summary>
public sealed class SimFrame
{
    public double TimeS { get; init; }
    public PackageSnapshot[] Packages { get; init; } = Array.Empty<PackageSnapshot>();
    public double[] ZoneSpeeds { get; init; } = Array.Empty<double>();
    /// <summary>
    /// Via del trasferitore su due inverter: velocità della parte di valle (dal punto di divisione al fermo finale).
    /// In <see cref="ZoneSpeeds"/> la via del trasferitore è la parte di monte.
    /// </summary>
    public double ValleSpeed { get; init; }
    public CycleState[] ZoneStates { get; init; } = Array.Empty<CycleState>();
    public bool[] PhotocellOutputs { get; init; } = Array.Empty<bool>();
    public StrapperState[] StrapperStates { get; init; } = Array.Empty<StrapperState>();
    public StopperState StopperState { get; init; }
    public TransferState TransferState { get; init; }
    public double TransferLiftM { get; init; }
    public double TransferLateralM { get; init; }
    public StrapperProcessState[] StrapperProcessStates { get; init; } = Array.Empty<StrapperProcessState>();
    public ClampState BundleClampState { get; init; }
    public double BundleClampClosed { get; init; }
    /// <summary>Stringifascio chiusi sul pacco (uno per posizione): gli altri restano aperti.</summary>
    public bool[] BundleClampActive { get; init; } = Array.Empty<bool>();
    public ClampState CompactorState { get; init; }
    public double CompactorClosed { get; init; }
    public RejectorState RejectorState { get; init; }
    public double RejectorStrokeM { get; init; }
    public StopState[] StopStates { get; init; } = Array.Empty<StopState>();
    public bool[] StopRaised { get; init; } = Array.Empty<bool>();
    public ScaleState[] ScaleStates { get; init; } = Array.Empty<ScaleState>();
}

/// <summary>
/// Collegamento logico del ciclo: QUANDO accade un evento (sulla riga <paramref name="CauseKey"/>) ALLORA parte un'azione
/// (sulla riga <paramref name="ActionKey"/>). Lo registra il motore nel punto in cui prende la decisione; le chiavi sono
/// quelle delle righe del ciclogramma. La stessa regola (<paramref name="RuleId"/>) si ripete a ogni ciclo.
/// </summary>
public sealed record LogicLink(double TimeS, string RuleId, string When, string Then, string CauseKey, string ActionKey, string? PackageId = null);

/// <summary>Registratore: tracce del ciclogramma (a segmenti) e fotogrammi periodici.</summary>
public sealed class SimulationRecorder
{
    private readonly Dictionary<string, CycleTrack> _byKey = new();

    public List<CycleTrack> Tracks { get; } = new();
    public List<SimFrame> Frames { get; } = new();
    /// <summary>Collegamenti logici evento → azione, in ordine di tempo.</summary>
    public List<LogicLink> Links { get; } = new();

    /// <summary>Finestra scorrevole: toglie fotogrammi, tratti e collegamenti finiti prima di <paramref name="fromS"/>.</summary>
    internal void TrimBefore(double fromS)
    {
        int n = 0;
        while (n < Frames.Count - 1 && Frames[n].TimeS < fromS) n++;
        if (n > 0) Frames.RemoveRange(0, n);
        foreach (var t in Tracks)
        {
            int k = 0;
            while (k < t.Segments.Count - 1 && t.Segments[k].EndS < fromS) k++;
            if (k > 0) t.Segments.RemoveRange(0, k);
        }
        Links.RemoveAll(l => l.TimeS < fromS);
    }

    public CycleTrack Track(string key, string label, TrackGroup group)
    {
        if (!_byKey.TryGetValue(key, out var t))
        {
            t = new CycleTrack(key, label, group);
            _byKey[key] = t;
            Tracks.Add(t);
        }
        return t;
    }

    internal void Set(string key, double time, CycleState state, string detail = "")
    {
        if (_byKey.TryGetValue(key, out var t)) t.Set(time, state, detail);
    }

    internal void CloseAll(double time)
    {
        foreach (var t in Tracks) t.Close(time);
    }

    /// <summary>Fotogramma più vicino precedente o uguale a t (ricerca binaria).</summary>
    public SimFrame? FrameAt(double t)
    {
        if (Frames.Count == 0) return null;
        int lo = 0, hi = Frames.Count - 1;
        if (t <= Frames[0].TimeS) return Frames[0];
        if (t >= Frames[hi].TimeS) return Frames[hi];
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Frames[mid].TimeS <= t) lo = mid; else hi = mid - 1;
        }
        return Frames[lo];
    }
}
