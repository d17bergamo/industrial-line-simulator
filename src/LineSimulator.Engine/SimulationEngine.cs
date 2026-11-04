using System.Globalization;
using LineSimulator.Core;
using LineSimulator.Engine.Logic;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;
using LineSimulator.Optimization;

namespace LineSimulator.Engine;

/// <summary>
/// Motore di simulazione a tempo discreto (passo fisso), a eventi e stati, deterministico e indipendente dalla UI.
///
/// Ciclo di scansione (come un PLC):
///  1. ingresso pacchi;  2. fotocellule (tempo di risposta, arrivo/presenza/uscita, identificazione all'ingresso);
///  3. prenotazione zone a pacco singolo, assegnazione dei fermi, abilitazione rigetto;
///  4. regole logiche (ingressi della scansione precedente);  5. comandi zone da valle a monte
///  (rigetto, legatura, fermi/stopper, accumulo, arresto automatico, interblocchi);  6. rampe inverter;
///  7. moto pacchi (trascinamento con limite d'attrito, battute, contatti e distanze);
///  8. macchine (stazione KNCA + KNOF + compattatore, legatrici, rigettatrice SMA, stopper, fermi,
///  trasferitore, bilici);  9. statistiche e registrazione (ciclogramma e fotogrammi 3D dallo stesso stato).
///
/// Ogni movimento dipende da sensori, consensi e interblocchi: non esistono animazioni temporizzate indipendenti.
/// Nessuna sorgente casuale: a parità di progetto i risultati sono identici.
/// </summary>
public sealed partial class SimulationEngine : ILogicContext
{
    private const double SpeedEps = 1e-4;

    private readonly List<ZoneRuntime> _zones = new();
    private readonly Dictionary<string, ZoneRuntime> _zoneById = new();
    private readonly List<(double Start, double End, ZoneRuntime Zone, bool Enabled)> _sections = new();
    private readonly List<PhotocellRuntime> _photocells = new();
    private readonly Dictionary<string, PhotocellRuntime> _pcById = new();
    private readonly List<StrapperRuntime> _strappers = new();
    private readonly List<PackageRuntime> _packages = new();
    private readonly List<PackageRuntime> _active = new();
    private readonly Dictionary<string, InverterRequests> _requests = new();
    private readonly Dictionary<string, StrategyComparison> _plans = new();
    private HashSet<string> _signals = new();
    private HashSet<string> _nextSignals = new();
    private readonly HashSet<string> _reportedOnce = new();
    private readonly List<SimEvent> _events = new();
    private readonly List<PackageType> _typeSequence = new();
    private readonly Dictionary<ZoneRuntime, HashSet<PackageRuntime>> _zoneOccupants = new();
    private readonly Dictionary<int, double> _weights = new();

    private long _step;
    private long _eventSeq;
    private int _nextIndex;
    private bool _infeedWaiting;
    private double _lastProgressTime;
    private double _nextRecordTime;
    private readonly double _recordInterval;
    private readonly double _minSafety;
    private readonly double _frictionDecel;
    private readonly double _transferClearanceM;
    private readonly ZoneRuntime? _stationUpstream;

    public SimulationEngine(LineProject project)
    {
        Project = project;
        // Preparazione del ciclo: nomi progressivi, collegamenti, stazione che segue le legatrici, sensori automatici,
        // legatura obbligatoria con legatrici abilitate (vedi SimulationPreparation)
        Preparation = SimulationPreparation.Prepare(project);
        AutoAddedSensors = Preparation.Changes;
        Validation = ProjectValidator.Validate(project);
        Topology = new LineTopology(project);
        Dt = Math.Clamp(project.Simulation.TimeStepMs, 0.1, 100) / 1000.0;
        _recordInterval = Math.Max(Dt, project.Simulation.RecordIntervalMs / 1000.0);
        _minSafety = project.Simulation.MinSafetyDistanceMm / 1000.0;
        _frictionDecel = Math.Max(1e-6, project.Simulation.MaxFrictionDeceleration);
        _transferClearanceM = project.PackageTypes.Count > 0 ? project.PackageTypes.Max(t => t.GraphicWidthMm) / 1000.0 : 0.6;

        foreach (var z in Topology.Zones)
        {
            var zr = new ZoneRuntime(z);
            _zones.Add(zr);
            _zoneById[z.Id] = zr;
            _requests[z.Id] = new InverterRequests();
            _zoneOccupants[zr] = new HashSet<PackageRuntime>();
            foreach (var s in z.Sections)
            {
                _sections.Add((s.StartMm / 1000.0, s.EndMm / 1000.0, zr, s.IsEnabled));
                // Alias: un riferimento all'ID del modulo indica la sua zona di comando
                if (s.Id != z.Id && !string.IsNullOrEmpty(s.Id)) { _zoneById.TryAdd(s.Id, zr); _requests.TryAdd(s.Id, _requests[z.Id]); }
            }
        }
        _sections.Sort((a, b) => a.Start.CompareTo(b.Start));

        foreach (var pc in project.Photocells)
        {
            var r = new PhotocellRuntime(pc) { ControlledZoneId = Topology.ResolveZoneId(pc.ControlledInverterId) };
            _photocells.Add(r);
            _pcById[pc.Id] = r;
        }
        // Arresto automatico e ripartenza: fotocellule della zona e fotocellula a monte (la più vicina prima dell'inizio zona)
        foreach (var zr in _zones)
        {
            zr.OwnPhotocells = _photocells.Where(pc => pc.Config.IsEnabled && pc.PositionM >= zr.Info.StartM - 1e-9 && pc.PositionM <= zr.Info.EndM + 1e-9).ToList();
            zr.RestartPhotocell = _photocells.Where(pc => pc.Config.IsEnabled && pc.PositionM < zr.Info.StartM - 1e-9)
                .OrderByDescending(pc => pc.PositionM).FirstOrDefault();
        }
        for (int i = 0; i < project.Strappers.Count; i++)
            _strappers.Add(new StrapperRuntime(project.Strappers[i], i));

        Stopper = new StopperRuntime(project.Stopper);
        Transfer = new TransferRuntime(project.Transfer);

        if (Topology.StrappingZone != null && project.StrappingStation.IsEnabled)
        {
            Station = new StationRuntime(_zoneById[Topology.StrappingZone.Id], project.StrappingStation.PositioningSpeed,
                project.StrappingStation.PositioningToleranceMm / 1000.0);
            var upZone = Topology.Upstream(Topology.StrappingZone);
            if (upZone != null) _stationUpstream = _zoneById[upZone.Id];
        }

        InitDevices();

        foreach (var id in project.Infeed.TypeSequence)
        {
            var t = project.PackageTypes.FirstOrDefault(x => x.Id == id);
            if (t != null) _typeSequence.Add(t);
        }
        if (_typeSequence.Count == 0 && project.PackageTypes.Count > 0) _typeSequence.Add(project.PackageTypes[0]);
        try { foreach (var kv in ProcessText.ParseWeights(project.Infeed.WeightOverrides)) _weights[kv.Key] = kv.Value; }
        catch (FormatException) { /* segnalato dal validatore */ }

        InitRecorder();

        foreach (var c in Preparation.Changes)
            Emit(SimEventType.ConfigurationError, EventSeverity.Info, "Preparazione del ciclo: " + c);
        foreach (var (sev, msg) in _bankingNotes)
            Emit(SimEventType.ConfigurationError, sev, msg, Project.Transfer.Id);
        if (Validation.HasErrors || Preparation.HasErrors)
        {
            foreach (var e in Validation.Errors)
                Emit(SimEventType.ConfigurationError, EventSeverity.Error, e.Message, e.ComponentId);
            foreach (var e in Preparation.Errors)
                Emit(SimEventType.ConfigurationError, EventSeverity.Error, e);
            Finish("Configurazione non valida: correggere gli errori prima di simulare.", false);
            return;
        }

        // Piani di legatura per tipologia (calcolati una volta, deterministici)
        if (Station != null)
        {
            foreach (var t in project.PackageTypes.Where(t => t.StrapPositionsMm.Count > 0))
            {
                var cmp = StrappingPlanner.Compare(project, Topology, t);
                _plans[t.Id] = cmp;
                if (!cmp.Selected.IsFeasible)
                    Emit(SimEventType.StrappingPlanError, EventSeverity.Error,
                        $"Piano di legatura non ammissibile per {t.Id}: {cmp.Selected.InfeasibleReason}", t.Id);
            }
        }

        foreach (var z in _zones) z.Level = z.Config.DefaultSpeed;
        RecordStates();
        CaptureFrameToRecorder();
        _nextRecordTime = _recordInterval;
    }

    // ------------------------------------------------------------------ API pubblica

    public LineProject Project { get; }
    public LineTopology Topology { get; }
    public ValidationReport Validation { get; }
    public double Dt { get; }
    public double Time => _step * Dt;
    public long StepCount => _step;
    public bool IsFinished { get; private set; }
    public bool CompletedNormally { get; private set; }
    public string? FinishReason { get; private set; }

    public IReadOnlyList<ZoneRuntime> Zones => _zones;
    public IReadOnlyList<PhotocellRuntime> Photocells => _photocells;
    public IReadOnlyList<StrapperRuntime> Strappers => _strappers;
    public IReadOnlyList<PackageRuntime> Packages => _packages;
    public StopperRuntime Stopper { get; }
    public TransferRuntime Transfer { get; }
    public StationRuntime? Station { get; }
    public IReadOnlyDictionary<string, StrategyComparison> StrappingPlans => _plans;
    public IReadOnlyList<SimEvent> Events => _events;
    public SimulationRecorder Recorder { get; } = new();
    public IReadOnlyCollection<string> ActiveSignals => _signals;
    /// <summary>Correzioni applicate al progetto all'avvio (descrizioni), compresi i sensori aggiunti automaticamente.</summary>
    public IReadOnlyList<string> AutoAddedSensors { get; }
    /// <summary>Esito della preparazione del ciclo (correzioni ed errori).</summary>
    public PreparationReport Preparation { get; }

    // Contatori anomalie
    public int Collisions { get; private set; }
    public int SafetyViolations { get; private set; }
    public int SlipEvents { get; private set; }
    public int ExcessiveImpacts { get; private set; }
    public int PositioningErrors { get; private set; }
    public int ZoneOverfills { get; private set; }
    public int TransferInterferences { get; private set; }
    public bool Deadlocked { get; private set; }
    public int AccumulationStops { get; private set; }
    public int MaxSimultaneousAccumulated { get; private set; }
    public double InfeedWaitingS { get; private set; }
    public int PackagesToSimulate => Project.Infeed.PackageCount;

    /// <summary>Esegue un passo di integrazione. Restituisce false se la simulazione è terminata.</summary>
    public bool Step()
    {
        if (IsFinished) return false;
        double t0 = Time;
        double dt = Dt;

        Infeed(t0);
        UpdatePhotocells(t0);
        UpdateReservations(t0);
        UpdateRejectorEnable(t0);
        EvaluateLogic();
        ComputeZoneCommands(t0, dt);
        var caps = HandoverSpeedCaps();
        foreach (var z in _zones) z.UpdateDrive(dt, Stopper.Config.AlignmentRollerSpeed, caps.TryGetValue(z, out var cap) ? cap : double.PositiveInfinity);
        MovePackages(t0, dt);

        _step++;
        double t1 = Time;
        UpdateZoneTracking(dt);
        UpdateStation(t1, dt);
        UpdateStrappers(t1, dt);
        UpdateClamps(dt);
        UpdateStrapperProcessStates(t1);
        UpdateRejector(t1, dt);
        UpdateStopper(t1, dt);
        UpdateIntermediateStops(dt);
        UpdateTransfer(t1, dt);
        UpdateScales(t1, dt);
        UpdateStatistics(dt);
        RecordStates();
        if (t1 >= _nextRecordTime - 1e-9)
        {
            CaptureFrameToRecorder();
            _nextRecordTime += _recordInterval;
        }
        CheckTermination(t1);
        return !IsFinished;
    }

    /// <summary>Avanza fino all'istante indicato (o al termine).</summary>
    public void RunUntil(double timeS)
    {
        while (!IsFinished && Time < timeS - 1e-9) Step();
    }

    public void RunToCompletion()
    {
        while (Step()) { }
    }

    /// <summary>Modalità step-by-step: avanza fino al prossimo evento (esclusi quelli di traccia se richiesto).</summary>
    public SimEvent? StepToNextEvent(bool includeTrace = false, double maxAdvanceS = 600)
    {
        int start = _events.Count;
        double limit = Time + maxAdvanceS;
        while (!IsFinished && Time < limit)
        {
            Step();
            for (int i = start; i < _events.Count; i++)
                if (includeTrace || _events[i].Severity != EventSeverity.Trace) return _events[i];
            start = _events.Count;
        }
        return null;
    }

    public SimulationResults ComputeResults() => ResultsCalculator.Compute(this);

    public const double MinLineSpeedMps = 0.5;
    public const double MaxLineSpeedMps = 1.5;

    /// <summary>Velocità di trasporto impostata dal pop-up in simulazione (m/s); null = velocità dei singoli inverter.</summary>
    public double? LineSpeedMps { get; private set; }

    /// <summary>
    /// Imposta la velocità di linea (0,5 - 1,5 m/s) su tutte le vie a rulli, anche a simulazione in corso:
    /// la configurazione degli inverter viene riletta ad ogni scansione. Restituisce il valore applicato.
    /// </summary>
    public double SetLineSpeed(double mps)
    {
        double v = Math.Clamp(double.IsFinite(mps) ? mps : MinLineSpeedMps, MinLineSpeedMps, MaxLineSpeedMps);
        foreach (var z in _zones)
        {
            z.Config.HighSpeed = v;
            if (z.Config.LowSpeed > v) z.Config.LowSpeed = v;
        }
        LineSpeedMps = v;
        return v;
    }

    /// <summary>Produttività oraria teorica alla velocità corrente (pacchi/h), limitata dalla zona più lenta.</summary>
    public double TheoreticalPackagesPerHour(double packageLengthM, double gapM = 0.0)
    {
        if (_zones.Count == 0 || packageLengthM <= 0) return 0;
        double v = _zones.Min(z => z.Config.HighSpeed);
        return 3600.0 * v / (packageLengthM + Math.Max(0, gapM));
    }

    public SimFrame CaptureFrame() => new()
    {
        TimeS = Time,
        Packages = _active.Select(p => new PackageSnapshot(p.Id, p.Type.Id, p.HeadM, p.LengthM, p.Velocity, p.State,
            p.LiftM, p.LateralM, p.StrapsDone, p.StrapsTotal, p.Type.GraphicWidthMm, p.Type.GraphicHeightMm)).ToArray(),
        ZoneSpeeds = _zones.Select(z => z.ActualSpeed).ToArray(),
        ZoneStates = _zones.Select(z => z.CycleStateCode).ToArray(),
        PhotocellOutputs = _photocells.Select(p => p.Output).ToArray(),
        StrapperStates = _strappers.Select(s => s.State).ToArray(),
        StopperState = Stopper.State,
        TransferState = Transfer.State,
        TransferLiftM = Transfer.LiftM,
        TransferLateralM = Transfer.LateralM,
        StrapperProcessStates = _strappers.Select(s => s.ProcessState).ToArray(),
        BundleClampState = BundleClamp?.State ?? ClampState.Open,
        BundleClampClosed = BundleClamp?.ClosedFraction ?? 0,
        CompactorState = Compactor?.State ?? ClampState.Open,
        CompactorClosed = Compactor?.ClosedFraction ?? 0,
        RejectorState = Rejector?.State ?? RejectorState.Idle,
        RejectorStrokeM = Rejector?.StrokeM ?? 0,
        StopStates = _stops.Select(s => s.State).ToArray(),
        StopRaised = _stops.Select(s => s.Raised).ToArray(),
        ScaleStates = _scales.Select(s => s.State).ToArray()
    };

    public ZoneRuntime? Zone(string id) => _zoneById.TryGetValue(id, out var z) ? z : null;

    // ------------------------------------------------------------------ 1. Ingresso

    private double NextDueTime() => Project.Infeed.FirstEntryTimeS + _nextIndex * Project.Infeed.IntervalS;

    /// <summary>Posizione (m) della fotocellula di ingresso della via rulli di legatura.</summary>
    private double StrappingEntryM
    {
        get
        {
            var zi = Station?.Zone.Info;
            var inZone = zi == null ? null : _photocells.Where(pc => pc.Config.IsEnabled && pc.PositionM >= zi.StartM - 1e-6 && pc.PositionM <= zi.EndM + 1e-6)
                                                        .OrderBy(pc => pc.PositionM).FirstOrDefault();
            return inZone?.PositionM ?? _photocells.FirstOrDefault(pc => pc.IsEntry)?.PositionM ?? Topology.LineStartM;
        }
    }

    /// <summary>Posizione (m) della fotocellula di uscita della via rulli di legatura (fine della zona se manca).</summary>
    private double StrappingExitM
    {
        get
        {
            var tagged = _photocells.Where(pc => pc.Config.IsEnabled && pc.Config.Purpose == PhotocellPurpose.StrappingExit).OrderBy(pc => pc.PositionM).LastOrDefault();
            if (tagged != null) return tagged.PositionM;
            var zi = Station?.Zone.Info ?? (_zones.Count > 0 ? _zones[0].Info : null);
            if (zi == null) return Topology.LineStartM;
            var inZone = _photocells.Where(pc => pc.Config.IsEnabled && !pc.IsEntry && pc.PositionM >= zi.StartM - 1e-6 && pc.PositionM <= zi.EndM + 1e-6)
                                    .OrderBy(pc => pc.PositionM).LastOrDefault();
            return inZone?.PositionM ?? zi.EndM;
        }
    }

    private void Infeed(double t)
    {
        if (Project.Infeed.Mode == InfeedMode.AfterStrapping) { InfeedAfterStrapping(t); return; }
        var cfg = Project.Infeed;
        if (_nextIndex >= cfg.PackageCount || _typeSequence.Count == 0) return;
        double due = NextDueTime();
        if (t < due - 1e-9) return;

        var type = _typeSequence[_nextIndex % _typeSequence.Count];
        double L = type.LengthMm / 1000.0;
        double start = Topology.LineStartM;
        double head = start + L;
        double gap = cfg.MinimumGapMm / 1000.0;
        bool free = _active.Where(p => p.OnRollers).All(p => p.TailM >= head + gap - 1e-9 || p.HeadM <= start - 1e-9);
        if (!free)
        {
            InfeedWaitingS += Dt;
            if (!_infeedWaiting)
            {
                _infeedWaiting = true;
                Emit(SimEventType.InfeedWaiting, EventSeverity.Info, $"Ingresso pacco {_nextIndex + 1} in attesa: spazio in ingresso non libero.");
            }
            return;
        }
        _infeedWaiting = false;
        var pkg = new PackageRuntime(_nextIndex + 1, type)
        {
            HeadM = head,
            EntryTimeS = t,
            StrapsTotal = type.StrapPositionsMm.Count
        };
        if (_weights.TryGetValue(pkg.Sequence, out var w)) pkg.WeightKg = w;
        pkg.StrapStatus = Station != null && pkg.StrapsTotal > 0 ? StrapStatus.Pending : StrapStatus.NotRequired;
        pkg.Velocity = DriveSpeed(pkg);
        _nextIndex++;
        _packages.Add(pkg);
        _active.Add(pkg);
        _lastProgressTime = t;
        Emit(SimEventType.PackageEntered, EventSeverity.Info, $"Ingresso {pkg.Id} ({type.Id}, {type.LengthMm:0} mm, {pkg.WeightKg:0} kg)", null, pkg.Id);
    }

    /// <summary>
    /// Ingresso "un pacco alla volta nella via rulli di legatura": il nuovo pacco si crea a monte della linea quando la coda del
    /// precedente libera la fotocellula di ingresso della via rulli di legatura, più il ritardo impostato; avanza solo quando la
    /// coda del precedente libera la fotocellula di uscita della via rulli di legatura.
    /// </summary>
    private void InfeedAfterStrapping(double t)
    {
        var cfg = Project.Infeed;
        // Consenso ad avanzare al pacco in attesa
        var waiting = _active.FirstOrDefault(p => !p.InfeedReleased);
        if (waiting != null)
        {
            var prev = _packages.FirstOrDefault(p => p.Sequence == waiting.Sequence - 1);
            bool clear = prev == null || !prev.OnRollers || prev.TailM > StrappingExitM + 1e-9;
            if (!clear) { InfeedWaitingS += Dt; return; }
            waiting.InfeedReleased = true;
            _lastProgressTime = t;
            Emit(SimEventType.InfeedWaiting, EventSeverity.Info,
                prev == null ? $"{waiting.Id} avanza nella via rulli di legatura"
                             : $"{waiting.Id} avanza: la coda di {prev.Id} ha liberato la fotocellula di uscita legatura ({StrappingExitM * 1000:0} mm)", null, waiting.Id);
            return;
        }
        if (_nextIndex >= cfg.PackageCount || _typeSequence.Count == 0) return;
        var last = _packages.LastOrDefault();
        if (last == null)
        {
            if (t < cfg.FirstEntryTimeS - 1e-9) return;
        }
        else
        {
            if (last.EntryClearedTimeS == null)
            {
                if (last.OnRollers && last.TailM <= StrappingEntryM + 1e-9) return;
                last.EntryClearedTimeS = t;
            }
            if (t < last.EntryClearedTimeS.Value + Math.Max(0, cfg.NewPackageDelayS) - 1e-9) return;
        }

        var type = _typeSequence[_nextIndex % _typeSequence.Count];
        double start = Topology.LineStartM;
        // Il nuovo pacco attende sul trasporto di alimentazione, almeno alla distanza di sicurezza dalla coda del precedente
        if (last != null && last.OnRollers && last.TailM < start + _minSafety) start = last.TailM - _minSafety;
        var pkg = new PackageRuntime(_nextIndex + 1, type)
        {
            HeadM = start,               // creato a monte della linea: testa all'inizio della via rulli di legatura (o più indietro)
            EntryTimeS = t,
            StrapsTotal = type.StrapPositionsMm.Count,
            InfeedReleased = false
        };
        if (_weights.TryGetValue(pkg.Sequence, out var w)) pkg.WeightKg = w;
        pkg.StrapStatus = Station != null && pkg.StrapsTotal > 0 ? StrapStatus.Pending : StrapStatus.NotRequired;
        pkg.Velocity = 0;
        _nextIndex++;
        _packages.Add(pkg);
        _active.Add(pkg);
        _lastProgressTime = t;
        Emit(SimEventType.PackageEntered, EventSeverity.Info,
            $"Creato {pkg.Id} ({type.Id}, {type.LengthMm:0} mm, {pkg.WeightKg:0} kg) in ingresso" +
            (last != null ? $": coda di {last.Id} uscita dalla fotocellula di ingresso legatura + {cfg.NewPackageDelayS:0.#} s" : ""), null, pkg.Id);
        InfeedAfterStrapping(t);   // il primo pacco (o con il precedente già uscito) avanza subito
    }

    // ------------------------------------------------------------------ 2. Fotocellule

    private void UpdatePhotocells(double t)
    {
        foreach (var pc in _photocells)
        {
            PackageRuntime? found = null;
            foreach (var p in _active)
            {
                bool detectable = p.OnRollers || (p.State == PackageState.OnTransfer && p.LateralM < 0.01);
                if (detectable && p.TailM <= pc.PositionM && p.HeadM >= pc.PositionM) { found = p; break; }
            }
            if (!pc.Update(t, found)) continue;
            var pk = pc.OutputPackage;
            var sev = pc.IsEntry ? EventSeverity.Info : EventSeverity.Trace;
            if (pc.Output)
            {
                pc.Activations++;
                Emit(SimEventType.PhotocellArrival, sev, $"{pc.Id} OCCUPATA: arrivo {pk?.Id}", pc.Id, pk?.Id);
                if (pc.IsEntry && pk != null && !pk.Identified) Identify(pk, pc, t);
                if (pc.Config.Purpose == PhotocellPurpose.StrappingReference && pk != null) StrappingReference(pk, pc);
            }
            else
            {
                string len = "";
                if (pc.IsEntry && pk != null)
                {
                    pk.MeasuredLengthM = pk.HeadM - pc.PositionM;
                    len = $", lunghezza rilevata {pk.MeasuredLengthM.Value * 1000:0} mm";
                }
                Emit(SimEventType.PhotocellCleared, sev, $"{pc.Id} LIBERA: uscita completa di {pk?.Id}{len}", pc.Id, pk?.Id);
            }
        }
    }

    /// <summary>Identificazione del pacco alla fotocellula di ingresso (tracciamento, rigetto).</summary>
    private void Identify(PackageRuntime p, PhotocellRuntime pc, double t)
    {
        p.Identified = true;
        p.IdentifiedTimeS = t;
        p.EntryTrackStartHeadM = p.HeadM;
        Emit(SimEventType.PackageIdentified, EventSeverity.Info,
            $"{p.Id} identificato da {pc.Id}: tipologia {p.Type.Id}, {p.LengthM * 1000:0} mm, {p.WeightKg:0} kg", pc.Id, p.Id);
        if (Rejector != null && Rejector.ShouldReject(p))
        {
            p.RejectRequested = true;
            Emit(SimEventType.RejectorPhase, EventSeverity.Info, $"{Rejector.Id}: {p.Id} identificato come pacco da rigettare", Rejector.Id, p.Id);
        }
    }

    // ------------------------------------------------------------------ 3. Prenotazioni zone a pacco singolo

    private void UpdateReservations(double t)
    {
        foreach (var z in _zones.Where(z => z.IsSinglePackage))
        {
            if (z.MultiStop) { UpdateMultiStopReservations(z); continue; }
            var res = z.ReservedBy;
            if (res != null)
            {
                bool release = z.Info.Kind == ZoneKind.Stopper
                    ? !res.OnRollers && (!Transfer.Config.BlockInfeedUntilHome || Transfer.State == TransferState.Home)
                    : !res.OnRollers || res.TailM >= z.Info.EndM - 1e-9 || ReleasedFromStrappingZone(z, res);
                if (release)
                {
                    z.ReservedBy = null;
                    if (Station != null && z == Station.Zone && Station.Package == res)
                    {
                        Station.State = StationState.Idle;
                        Station.Package = null;
                        Station.Plan = null;
                        Station.PackagesProcessed++;
                    }
                }
            }
            if (z.ReservedBy == null)
            {
                var p = _active.FirstOrDefault(x => x.OnRollers && !_releasedFromStation.Contains(x) && x.HeadM > z.Info.StartM + 1e-9 && x.TailM < z.Info.EndM - 1e-9);
                if (p != null)
                {
                    z.ReservedBy = p;
                    z.Reservations++;
                    if (Station != null && z == Station.Zone) StationAccept(p, t);
                }
            }
            if (z.ReservedBy != null)
            {
                foreach (var other in _active)
                {
                    if (other == z.ReservedBy || !other.OnRollers || _releasedFromStation.Contains(other)) continue;
                    if (other.HeadM > z.Info.StartM + 1e-9 && other.TailM < z.Info.EndM - 1e-9 && _reportedOnce.Add($"overfill:{z.Id}:{other.Id}"))
                    {
                        ZoneOverfills++;
                        Emit(SimEventType.ZoneOverfilled, EventSeverity.Warning,
                            $"{other.Id} entrato nella zona {z.Id} riservata a {z.ReservedBy.Id}: verificare fotocellule di accumulo.", z.Id, other.Id);
                    }
                }
            }
        }
    }

    /// <summary>Pacchi già legati che hanno lasciato la via di legatura ma sono ancora nello svincolo sotto lo stesso inverter.</summary>
    private readonly HashSet<PackageRuntime> _releasedFromStation = new();

    /// <summary>
    /// Via di legatura e svincolo sotto un unico inverter: il pacco legato libera la stazione quando la sua coda supera la
    /// fotocellula di uscita della via di legatura (prima della fine della zona). Il pacco successivo può allora essere
    /// posizionato: fermando l'inverter si ferma anche il precedente sullo svincolo (stesso azionamento).
    /// </summary>
    private bool ReleasedFromStrappingZone(ZoneRuntime z, PackageRuntime res)
    {
        if (Station == null || z != Station.Zone || Station.Package != res || Station.State != StationState.Releasing) return false;
        double exit = StrappingExitM;
        if (exit >= z.Info.EndM - 1e-6 || res.TailM <= exit + 1e-9) return false;
        _releasedFromStation.Add(res);
        return true;
    }

    private void StationAccept(PackageRuntime p, double t)
    {
        var st = Station!;
        st.Package = p;
        st.StopIndex = 0;
        st.Plan = null;
        // Con le legatrici abilitate ogni pacco deve essere legato: un pacco non legabile resta fermo in stazione.
        bool machines = _strappers.Any(s => s.Config.IsEnabled);
        if (p.StrapStatus != StrapStatus.Pending && !(machines && p.StrapStatus == StrapStatus.NotRequired))
        {
            st.State = StationState.Releasing;
            return;
        }
        if (p.StrapStatus == StrapStatus.NotRequired || !_plans.TryGetValue(p.Type.Id, out var cmp) || !cmp.Selected.IsFeasible)
        {
            p.StrapStatus = StrapStatus.Failed;
            st.State = StationState.Blocked;
            p.State = PackageState.Strapping;
            Emit(SimEventType.StrappingPlanError, EventSeverity.Error,
                $"{p.Id} fermo in stazione: con le legatrici abilitate deve essere legato, ma per {p.Type.Id} non c'è un piano di legatura ammissibile.", st.Zone.Id, p.Id);
            return;
        }
        st.Plan = cmp.Selected;
        if (st.Plan.Stops.Count == 0)
        {
            p.StrapStatus = StrapStatus.Done;
            st.State = StationState.Releasing;
            return;
        }
        st.State = StationState.Positioning;
        st.TargetHeadM = st.Plan.Stops[0].HeadPositionM;
        p.StrapStatus = StrapStatus.InProgress;
        p.State = PackageState.Positioning;
        Emit(SimEventType.PositioningStarted, EventSeverity.Info,
            $"{p.Id} verso fermata 1/{st.Plan.Stops.Count} (testa a {st.TargetHeadM * 1000:0} mm)", st.Zone.Id, p.Id);
    }

    // ------------------------------------------------------------------ 4. Logica

    private void EvaluateLogic()
    {
        foreach (var r in _requests.Values) r.Clear();
        _nextSignals.Clear();

        foreach (var pc in _photocells)
        {
            if (!pc.Active || pc.Config.Action is PhotocellAction.None or PhotocellAction.Accumulation) continue;
            if (!_requests.TryGetValue(pc.ControlledZoneId, out var req)) continue;
            switch (pc.Config.Action)
            {
                case PhotocellAction.SlowDown: req.Low = true; req.SpeedReasons.Add($"Rallentamento {pc.Id}"); break;
                case PhotocellAction.SpeedUp: req.High = true; req.SpeedReasons.Add($"Alta {pc.Id}"); break;
                case PhotocellAction.Stop: req.Stop = true; req.StopReasons.Add($"Arresto {pc.Id}"); break;
            }
        }
        // Fotocellula di arresto davanti al trasferitore: con il trasferitore sollevato il materiale si ferma sulla via a rulli
        bool transferRaised = Transfer.State is TransferState.Lifting or TransferState.Translating or TransferState.WaitingDestination;
        foreach (var pc in _photocells)
        {
            if (pc.Config.Purpose != PhotocellPurpose.TransferStop || !pc.Config.IsEnabled || !pc.Output || !transferRaised) continue;
            if (_requests.TryGetValue(pc.ControlledZoneId, out var req)) { req.Stop = true; req.StopReasons.Add($"trasferitore sollevato ({pc.Id})"); }
        }
        LogicEvaluator.ApplyRules(Project.Rules, this, _requests, _nextSignals);
        (_signals, _nextSignals) = (_nextSignals, _signals);
    }

    // ------------------------------------------------------------------ 5. Comandi zone (da valle a monte)

    private void ComputeZoneCommands(double t, double dt)
    {
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            var req = _requests[z.Id];
            var prevMode = z.Mode; var prevLevel = z.Level; var prevReason = z.Reason;
            bool prevAuto = z.AutoStopped;
            bool autoStop = false;
            PackageRuntime? demandCause = null;
            InverterMode mode;
            string reason;
            z.BlockedByAccumulation = false;

            if (!z.Config.IsEnabled)
            {
                mode = InverterMode.Stop; reason = "Inverter disabilitato";
            }
            else if (Rejector != null && z == Rejector.Zone && Rejector.State == RejectorState.Positioning)
            {
                mode = InverterMode.Position; reason = $"{Rejector.Id}: arresto e posizionamento {Rejector.Package?.Id}";
                z.PositionCommand = Rejector.VelocityCommand(dt);
            }
            else if (Rejector != null && z == Rejector.Zone && Rejector.State is RejectorState.InPosition or RejectorState.Ejecting
                         or RejectorState.Returning or RejectorState.RestartConsent or RejectorState.Fault)
            {
                mode = InverterMode.Stop; reason = $"{Rejector.Id}: ciclo di rigetto in corso ({Rejector.State})";
            }
            else if (Station != null && z == Station.Zone && Station.State == StationState.Positioning)
            {
                mode = InverterMode.Position; reason = $"Posizionamento fermata {Station.StopIndex + 1}";
                z.PositionCommand = Station.VelocityCommand(dt);
            }
            else if (Station != null && z == Station.Zone && Station.State is StationState.Strapping or StationState.Compacting
                         or StationState.Clamping or StationState.Unclamping or StationState.WaitingStrappers or StationState.Blocked)
            {
                mode = InverterMode.Stop;
                reason = Station.State switch
                {
                    StationState.Compacting => "Compattazione laterale in corso",
                    StationState.Clamping => "Chiusura stringifascio in corso",
                    StationState.Unclamping => "Apertura stringifascio/compattatore",
                    StationState.WaitingStrappers => "Attesa carica filo / riarmo legatrici",
                    StationState.Blocked => "Pacco non legabile: fermo (legatura obbligatoria)",
                    _ => "Legatura in corso"
                };
            }
            else if (z.MultiStop)
            {
                mode = MultiStopCommand(z, t, dt, out reason, out autoStop);
            }
            else if (z.Info.Kind == ZoneKind.Stopper && Stopper.Package != null)
            {
                mode = InverterMode.Align; reason = Stopper.State == StopperState.Aligned ? "Allineato, attesa trasferitore" : "Allineamento allo stopper";
            }
            else if (z.Config.AutoStopWhenEmpty && ShouldAutoStop(z))
            {
                mode = InverterMode.Stop;
                reason = $"Arresto automatico: corsa a vuoto {z.EmptyTravelM:0.0} m ≥ lunghezza zona {z.Info.LengthM:0.0} m senza pacchi sulle fotocellule";
                autoStop = true;
            }
            else
            {
                mode = InverterMode.Run; reason = "Marcia";
                foreach (var pc in _photocells)
                {
                    if (pc.Config.Action != PhotocellAction.Accumulation || pc.ControlledZoneId != z.Id || !pc.Active) continue;
                    var p = pc.RawPackage ?? pc.LastPackage;
                    var next = i + 1 < _zones.Count ? _zones[i + 1] : null;
                    bool accept = next != null && next.AcceptsFor(p);
                    if (!accept && next != null && HandoverInProgress(z, next, p, dt))
                    {
                        // Un pacco a cavallo del confine è già stato accettato dalla zona a valle:
                        // la consegna ha priorità, purché il pacco in fotocellula possa ancora fermarsi in zona.
                        reason = "Consegna a valle in corso";
                        continue;
                    }
                    if (!accept)
                    {
                        mode = InverterMode.Stop;
                        reason = $"Accumulo {pc.Id}: zona a valle {(next == null ? "inesistente" : next.Id)} non disponibile";
                        z.BlockedByAccumulation = true;
                        break;
                    }
                }
            }

            if (req.Stop && mode != InverterMode.Stop)
            {
                mode = InverterMode.Stop;
                reason = "Interblocco: " + string.Join(", ", req.StopReasons);
            }
            if (req.Stop) autoStop = false;

            z.Mode = mode;
            z.Reason = reason;
            z.AutoStopped = autoStop && mode == InverterMode.Stop;
            if (mode == InverterMode.Run)
            {
                z.Level = req.Low ? SpeedLevel.Low : req.High ? SpeedLevel.High : z.Config.DefaultSpeed;
                if (req.SpeedReasons.Count > 0) z.Reason += " (" + string.Join(", ", req.SpeedReasons) + ")";
            }
            z.Accepting = z.AcceptsFor(null);

            if (z.AutoStopped && !prevAuto && t > 0)
                Emit(SimEventType.AutoStop, EventSeverity.Info, $"{z.Id}: arresto automatico (nessun pacco da movimentare)", z.Id);
            else if (prevAuto && !z.AutoStopped && mode != InverterMode.Stop)
            {
                var pcStart = z.RestartPhotocell != null && z.RestartPhotocell.Output ? z.RestartPhotocell : z.OwnPhotocells.FirstOrDefault(x => x.Output);
                demandCause = pcStart?.RawPackage ?? pcStart?.LastPackage;
                Emit(SimEventType.AutoStart, EventSeverity.Info,
                    $"{z.Id}: ripartenza comandata da {pcStart?.Id ?? "fotocellula"}{(demandCause != null ? $" (arrivo {demandCause.Id})" : "")}", z.Id, demandCause?.Id);
            }

            if (mode != prevMode || (mode == InverterMode.Run && z.Level != prevLevel) || (mode == InverterMode.Stop && reason != prevReason))
            {
                string what = mode switch
                {
                    InverterMode.Run => z.Level == SpeedLevel.High ? "MARCIA ALTA" : "MARCIA BASSA",
                    InverterMode.Stop => "ARRESTO",
                    InverterMode.Position => "POSIZIONAMENTO",
                    InverterMode.Reverse => "RITORNO INDIETRO",
                    _ => "ALLINEAMENTO"
                };
                Emit(SimEventType.InverterCommand, EventSeverity.Trace, $"{z.Id}: {what} - {z.Reason}", z.Id);
            }
        }
    }

    /// <summary>
    /// Arresto automatico (regola di linea): la zona si spegne quando i suoi rulli hanno compiuto una corsa pari alla
    /// lunghezza della zona senza che le sue fotocellule (né quella di ripartenza a monte) rilevino pacchi.
    /// Non si considerano la sezione precedente né i pacchi in arrivo: la ripartenza è comandata dalla fotocellula a monte.
    /// Salvaguardia di simulazione: se un pacco è fisicamente nella zona ma non è visto da alcuna fotocellula,
    /// l'arresto viene rinviato e segnalato (manca una fotocellula).
    /// </summary>
    private bool ShouldAutoStop(ZoneRuntime z)
    {
        if (!z.SensorsEmpty) return false;
        // Il primo modulo resta in marcia per accogliere il pacco che ha il consenso ad avanzare dall'ingresso
        if (z.Info.StartM <= Topology.LineStartM + 1e-6 && _active.Any(p => p.InfeedReleased && p.OnRollers && p.TailM < z.Info.StartM + 1e-6 && p.HeadM <= z.Info.EndM))
            return false;
        var hidden = _active.FirstOrDefault(p => p.OnRollers && p.HeadM > z.Info.StartM + 1e-9 && p.TailM < z.Info.EndM - 1e-9);
        if (hidden == null) return true;
        if (!z.UndetectedWarned)
        {
            z.UndetectedWarned = true;
            Emit(SimEventType.DeviceAlarm, EventSeverity.Warning,
                $"{z.Id}: {hidden.Id} nella zona non è rilevato da alcuna fotocellula dopo una corsa pari alla lunghezza della zona: arresto automatico rinviato, aggiungere una fotocellula.", z.Id, hidden.Id);
        }
        return false;
    }

    /// <summary>
    /// Vero se la zona sta consegnando alla zona a valle un pacco (diverso da quello in fotocellula) che la
    /// zona a valle ha accettato, e il pacco in fotocellula può ancora arrestarsi prima della fine zona
    /// (spazio di frenatura + due scansioni). Senza questa priorità il pacco in consegna resterebbe
    /// frenato dai rulli fermi della zona a monte (blocco).
    /// </summary>
    private bool HandoverInProgress(ZoneRuntime z, ZoneRuntime next, PackageRuntime? atPhotocell, double dt)
    {
        PackageRuntime? handed = null;
        foreach (var h in _active)
        {
            if (!h.OnRollers || h == atPhotocell) continue;
            if (h.TailM < z.Info.EndM - 1e-9 && h.HeadM > z.Info.EndM + 1e-9 && next.AcceptsFor(h)) { handed = h; break; }
        }
        if (handed == null) return false;
        if (atPhotocell == null) return true;
        double v = z.ActualSpeed;
        double stopAt = atPhotocell.HeadM + Kinematics.BrakingDistance(v, z.Config.Deceleration) + 2 * v * dt;
        return stopAt < z.Info.EndM - 1e-3;
    }

    // ------------------------------------------------------------------ 7. Moto pacchi

    /// <summary>
    /// Passaggio tra moduli: se un pacco sta a cavallo tra un modulo a monte e uno a valle e sul modulo a valle non ci sono
    /// altri pacchi, il modulo a valle non corre più veloce di quello a monte (sincronismo durante il passaggio). Così il
    /// pacco trattenuto dal modulo a monte (es. svincolo fermo durante la legatura) non slitta quando lo lascia.
    /// </summary>
    private Dictionary<ZoneRuntime, double> HandoverSpeedCaps()
    {
        var caps = new Dictionary<ZoneRuntime, double>();
        foreach (var p in _active)
        {
            if (!p.OnRollers) continue;
            ZoneRuntime? prev = null;
            foreach (var s in _sections)
            {
                if (s.End <= p.TailM + 1e-9 || s.Start >= p.HeadM - 1e-9) continue;
                if (prev != null && s.Zone != prev)
                {
                    var down = s.Zone;
                    bool others = _active.Any(q => q != p && q.OnRollers && q.HeadM > down.Info.StartM + 1e-9 && q.TailM < down.Info.EndM - 1e-9);
                    if (!others)
                        caps[down] = Math.Min(caps.TryGetValue(down, out var c) ? c : double.PositiveInfinity, prev.ActualSpeed);
                }
                prev = s.Zone;
            }
        }
        return caps;
    }

    /// <summary>Velocità di trascinamento: minimo tra le velocità delle sezioni sotto il pacco.</summary>
    private double DriveSpeed(PackageRuntime p)
    {
        double v = double.PositiveInfinity;
        bool any = false;
        foreach (var s in _sections)
        {
            if (s.End <= p.TailM + 1e-9 || s.Start >= p.HeadM - 1e-9) continue;
            any = true;
            double sv = s.Enabled ? s.Zone.ActualSpeed : 0;
            if (sv < v) v = sv;
        }
        if (any) return v;
        // Pacco in ingresso a monte della linea: avanza con la via rulli di legatura solo dopo il consenso
        if (p.InfeedReleased && p.HeadM <= Topology.LineStartM + 1e-6 && _sections.Count > 0)
            return _sections[0].Enabled ? _sections[0].Zone.ActualSpeed : 0;
        return 0;
    }

    private void MovePackages(double t, double dt)
    {
        PackageRuntime? ahead = null;
        double dvMax = _frictionDecel * dt;
        bool transferInLine = Transfer.State == TransferState.Lifting
            || ((Transfer.State == TransferState.Translating || Transfer.State == TransferState.Returning) && Transfer.LateralM < _transferClearanceM);

        foreach (var p in _active)
        {
            if (!p.OnRollers) continue;
            double vOld = p.Velocity;
            double vTarget = DriveSpeed(p);
            // Pacco ancora in parte sul trasporto di alimentazione (a monte della linea): accelera con la rampa della prima via
            if (p.TailM < Topology.LineStartM - 1e-6 && _sections.Count > 0)
                vTarget = Math.Min(vTarget, vOld + Math.Max(0.01, _sections[0].Zone.Config.Acceleration) * dt);
            double vNew = Math.Clamp(vTarget, vOld - dvMax, vOld + dvMax);
            // In battuta (stopper o fermo) i rulli slittano volutamente sotto il pacco: non è un'anomalia.
            bool restrained = p.InStopperContact || p.InIntermediateStopContact || p.HeldByTailStop;
            bool slipping = !restrained && Math.Abs(vTarget - vNew) > 1e-9;
            if (slipping && !p.IsSlipping && Math.Abs(vTarget - vOld) > 0.05)   // sotto 0,05 m/s è l'arresto a fine posizionamento
            {
                SlipEvents++;
                Emit(SimEventType.Slip, EventSeverity.Warning,
                    $"{p.Id} slitta sui rulli: variazione richiesta {Math.Abs(vTarget - vOld):0.###} m/s oltre il limite d'attrito.", null, p.Id);
            }
            p.IsSlipping = slipping;

            double newHead = p.HeadM + 0.5 * (vOld + vNew) * dt;

            // Battuta stopper
            if (Stopper.Raised && newHead >= Stopper.PositionM - 1e-9 && p.HeadM <= Stopper.PositionM + 1e-6)
            {
                if (!p.InStopperContact)
                {
                    double impact = vNew;
                    p.InStopperContact = true;
                    Emit(SimEventType.StopperContact, EventSeverity.Info, $"{p.Id} in battuta allo stopper a {impact:0.###} m/s", Project.Stopper.Id, p.Id);
                    if (impact > Stopper.Config.MaxImpactSpeed + 1e-9)
                    {
                        ExcessiveImpacts++;
                        Emit(SimEventType.ExcessiveImpact, EventSeverity.Warning,
                            $"Impatto di {p.Id} allo stopper a {impact:0.###} m/s > {Stopper.Config.MaxImpactSpeed:0.###} m/s ammessi.", Project.Stopper.Id, p.Id);
                    }
                }
                newHead = Stopper.PositionM;
                vNew = 0;
            }

            // Battuta su fermo scomparente con intestatura di testa (solo il pacco assegnato, fermo sollevato)
            foreach (var s in _stops)
            {
                if (s.Package != p || s.Reference != StopReference.Head || !s.Raised) continue;
                if (newHead >= s.PositionM - 1e-9 && p.HeadM <= s.PositionM + 1e-6)
                {
                    if (!p.InIntermediateStopContact)
                    {
                        p.InIntermediateStopContact = true;
                        if (vNew > Stopper.Config.MaxImpactSpeed + 1e-9)
                        {
                            ExcessiveImpacts++;
                            Emit(SimEventType.ExcessiveImpact, EventSeverity.Warning,
                                $"Impatto di {p.Id} al fermo {s.Id} a {vNew:0.###} m/s.", s.Id, p.Id);
                        }
                    }
                    newHead = s.PositionM;
                    vNew = 0;
                }
            }
            // Intestatura di coda: pacco trattenuto in posizione
            if (p.HeldByTailStop)
            {
                newHead = p.HeadM;
                vNew = 0;
            }

            // Contatto con il pacco a valle
            if (ahead != null && ahead.OnRollers)
            {
                double gap = ahead.TailM - newHead;
                if (gap < -1e-9)
                {
                    if (p.ContactWith != ahead.Id)
                    {
                        p.ContactWith = ahead.Id;
                        Collisions++;
                        Emit(SimEventType.Collision, EventSeverity.Error,
                            $"COLLISIONE: {p.Id} urta {ahead.Id} a {Math.Max(0, vNew - ahead.Velocity):0.###} m/s relativi (posizione {ahead.TailM * 1000:0} mm).", null, p.Id);
                    }
                    newHead = ahead.TailM;
                    vNew = Math.Min(vNew, ahead.Velocity);
                }
                else
                {
                    if (gap > 1e-6) p.ContactWith = null;
                    if (gap < _minSafety - 1e-9)
                    {
                        if (p.SafetyViolationWith != ahead.Id && gap >= -1e-9)
                        {
                            p.SafetyViolationWith = ahead.Id;
                            SafetyViolations++;
                            Emit(SimEventType.SafetyDistanceViolation, EventSeverity.Warning,
                                $"Distanza {p.Id}-{ahead.Id} = {gap * 1000:0} mm < minimo {_minSafety * 1000:0} mm.", null, p.Id);
                        }
                    }
                    else if (gap > _minSafety + 0.01) p.SafetyViolationWith = null;
                }
            }

            // Interferenza col trasferitore
            if (transferInLine && !Transfer.Packages.Contains(p) && newHead > Transfer.StartM && p.TailM < Transfer.EndM)
            {
                if (!p.TransferInterference)
                {
                    p.TransferInterference = true;
                    TransferInterferences++;
                    Collisions++;
                    Emit(SimEventType.TransferInterference, EventSeverity.Error,
                        $"COLLISIONE: {p.Id} entra nell'area del trasferitore mentre è in movimento ({Transfer.State}).", Project.Transfer.Id, p.Id);
                }
            }
            else if (!transferInLine) p.TransferInterference = false;

            p.HeadM = newHead;
            p.Velocity = vNew;
            if (Math.Abs(vNew) > SpeedEps) _lastProgressTime = t + dt;

            if (p.State == PackageState.Transport || p.State == PackageState.Accumulated)
            {
                var ns = p.IsStopped ? PackageState.Accumulated : PackageState.Transport;
                if (ns == PackageState.Accumulated && p.State == PackageState.Transport && vOld > SpeedEps)
                {
                    p.AccumulationStops++;
                    AccumulationStops++;
                    Emit(SimEventType.AccumulationStop, EventSeverity.Info, $"{p.Id} fermo in accumulo a {p.HeadM * 1000:0} mm", null, p.Id);
                }
                p.State = ns;
            }
            ahead = p;

            if (Project.Simulation.StopOnCollision && Collisions > 0)
            {
                Finish("Simulazione arrestata alla prima collisione.", false);
            }
        }
    }

    /// <summary>Tracciamento per zona: evento di uscita completa del pacco dalla sezione comandata.</summary>
    private void UpdateZoneTracking(double dt)
    {
        foreach (var z in _zones)
        {
            // Corsa a vuoto: si azzera quando una fotocellula della zona o quella di ripartenza a monte vede un pacco
            if (z.OwnPhotocells.Any(pc => pc.Output) || (z.RestartPhotocell?.Output ?? false)) z.EmptyTravelM = 0;
            else z.EmptyTravelM += z.ActualSpeed * dt;
            var set = _zoneOccupants[z];
            foreach (var p in set.ToList())
            {
                bool inside = p.OnRollers && p.HeadM > z.Info.StartM + 1e-9 && p.TailM < z.Info.EndM - 1e-9;
                if (inside) continue;
                set.Remove(p);
                if (p.OnRollers && p.TailM >= z.Info.EndM - 1e-9)
                    Emit(SimEventType.ZoneCleared, EventSeverity.Trace, $"{z.Id}: {p.Id} uscito completamente dalla sezione", z.Id, p.Id);
            }
            foreach (var p in _active)
            {
                if (!p.OnRollers || !(p.HeadM > z.Info.StartM + 1e-9 && p.TailM < z.Info.EndM - 1e-9)) continue;
                if (set.Add(p)) z.PackagesEntered++;
            }
        }
    }

    // ------------------------------------------------------------------ 8. Macchine: legatrici e stopper

    private void UpdateStrappers(double t, double dt)
    {
        foreach (var s in _strappers)
        {
            if (s.AdvanceArming(dt))
                Emit(SimEventType.StrapperPhase, EventSeverity.Info, $"{s.Id}: {(s.IsSma ? "riarmata" : "filo caricato, legatrice armata")}", s.Id, Station?.Package?.Id);
            var pkgBefore = s.Package;
            var changed = s.Advance(dt);
            if (changed == StrapperState.Idle) Rearm(s, pkgBefore);
            if (s.FaultJustCleared)
            {
                s.FaultJustCleared = false;
                Emit(SimEventType.StrapperFault, EventSeverity.Info, $"{s.Id}: guasto ripristinato, ciclo di legatura riavviato", s.Id, s.Package?.Id);
            }
            if (changed != null)
            {
                string phase = changed switch
                {
                    StrapperState.Strapping => "legatura",
                    StrapperState.Returning => "ritorno",
                    StrapperState.Idle => "ciclo completato",
                    _ => "preparazione"
                };
                Emit(SimEventType.StrapperPhase, EventSeverity.Info, $"{s.Id}: {phase}", s.Id, s.Package?.Id);
            }
        }
    }

    /// <summary>
    /// Riarmo a fine ciclo. SMA: sempre in automatico. KNCA: in automatico solo se il pacco ha ancora legature da
    /// eseguire con questa macchina (stesso pattern); a pattern concluso attende il pacco successivo alla fotocellula di riferimento.
    /// </summary>
    private void Rearm(StrapperRuntime s, PackageRuntime? p)
    {
        bool again = s.IsSma;
        if (!again && Station?.Plan != null && p != null && Station.Package == p)
            again = Station.Plan.Stops.Skip(Station.StopIndex + 1).Any(st => st.Tasks.Any(k => k.StrapperId == s.Id));
        if (again && s.BeginArming())
            Emit(SimEventType.StrapperPhase, EventSeverity.Info, $"{s.Id}: {(s.IsSma ? "riarmo automatico" : "carica del filo per la legatura successiva")}", s.Id, p?.Id);
    }

    /// <summary>Fotocellula di riferimento dell'encoder: azzera il conteggio e avvia la carica del filo delle KNCA.</summary>
    private void StrappingReference(PackageRuntime p, PhotocellRuntime pc)
    {
        if (!p.EncoderReferenced)
        {
            p.EncoderReferenced = true;
            Emit(SimEventType.PositioningStarted, EventSeverity.Info, $"{p.Id} riferito all'encoder di legatura ({pc.Id})", pc.Id, p.Id);
        }
        if (p.StrapStatus is StrapStatus.Done or StrapStatus.NotRequired) return;
        foreach (var s in _strappers.Where(x => x.Config.IsEnabled && !x.IsSma && !x.Busy))
            if (s.BeginArming())
                Emit(SimEventType.StrapperPhase, EventSeverity.Info, $"{s.Id}: carica del filo (testa di {p.Id} sulla fotocellula {pc.Id})", s.Id, p.Id);
    }

    private void UpdateStopper(double t, double dt)
    {
        if (Stopper.Package == null)
        {
            var p = _active.FirstOrDefault(x => x.OnRollers && x.InStopperContact);
            if (p == null) return;
            Stopper.Package = p;
            Stopper.State = StopperState.Aligning;
            Stopper.ElapsedS = 0;
            Stopper.Packages++;
            p.State = PackageState.Aligning;
            return;
        }
        Stopper.OccupiedTimeS += dt;
        if (Stopper.State == StopperState.Aligning)
        {
            Stopper.ElapsedS += dt;
            if (Stopper.ElapsedS >= Stopper.AlignmentTotalS - 1e-9)
            {
                Stopper.State = StopperState.Aligned;
                Stopper.Package.State = PackageState.Aligned;
                Emit(SimEventType.AlignmentCompleted, EventSeverity.Info,
                    $"{Stopper.Package.Id} allineato ({Stopper.AlignmentTotalS:0.##} s)", Project.Stopper.Id, Stopper.Package.Id);
            }
        }
    }

    // ------------------------------------------------------------------ 8. Trasferitore

    private void UpdateTransfer(double t, double dt)
    {
        var tr = Transfer;
        var cfg = tr.Config;
        if (tr.State == TransferState.Home)
        {
            tr.LiftM = 0; tr.LateralM = 0;
            var batch = ReadyBatch(t);
            if (batch != null)
            {
                tr.Packages.Clear();
                tr.Packages.AddRange(batch);
                _batchWaitSince = null;
                SetTransferPhase(tr, TransferState.Lifting, t);
            }
            return;
        }

        tr.BusyTimeS += dt;
        if (tr.State == TransferState.WaitingDestination)
        {
            if (TryPlanScales(tr.Packages))
                SetTransferPhase(tr, TransferState.Lowering, t);
            return;
        }
        tr.ElapsedS += dt;
        double dur = tr.PhaseDuration(tr.State);
        double f = dur > 0 ? Math.Clamp(tr.ElapsedS / dur, 0, 1) : 1;
        double lift = cfg.LiftStrokeMm / 1000.0, stroke = cfg.TranslationStrokeMm / 1000.0;
        switch (tr.State)
        {
            case TransferState.Lifting: tr.LiftM = lift * f; break;
            case TransferState.Translating: tr.LiftM = lift; tr.LateralM = stroke * f; break;
            case TransferState.Lowering: tr.LiftM = lift * (1 - f); tr.LateralM = stroke; break;
            case TransferState.Depositing: tr.LiftM = 0; tr.LateralM = stroke; break;
            case TransferState.Returning: tr.LiftM = 0; tr.LateralM = stroke * (1 - f); break;
        }
        if (tr.State != TransferState.Returning)
        {
            foreach (var pk in tr.Packages)
            {
                pk.LiftM = tr.State == TransferState.Depositing ? 0 : tr.LiftM;
                pk.LateralM = tr.LateralM;
            }
        }
        if (tr.ElapsedS < dur - 1e-9) return;

        switch (tr.State)
        {
            case TransferState.Lifting:
                foreach (var pk in tr.Packages)
                {
                    pk.State = PackageState.OnTransfer;
                    pk.InStopperContact = false;
                    pk.InIntermediateStopContact = false;
                    pk.HeldByTailStop = false;
                    pk.AssignedStop = -1;
                    pk.Velocity = 0;
                }
                if (Stopper.Package == null || tr.Packages.Contains(Stopper.Package))
                {
                    Stopper.Package = null;
                    Stopper.State = StopperState.Free;
                }
                ReleaseStopsFor(tr.Packages);
                SetTransferPhase(tr, TransferState.Translating, t);
                break;
            case TransferState.Translating:
                if (_scales.Count > 0 && !TryPlanScales(tr.Packages))
                {
                    tr.State = TransferState.WaitingDestination;
                    tr.ElapsedS = 0;
                    Emit(SimEventType.TransferWaiting, EventSeverity.Info, "Trasferitore: attesa bilici di destinazione liberi (interblocco)", Project.Transfer.Id, tr.Package?.Id);
                    break;
                }
                SetTransferPhase(tr, TransferState.Lowering, t);
                break;
            case TransferState.Lowering:
                SetTransferPhase(tr, TransferState.Depositing, t);
                break;
            case TransferState.Depositing:
                foreach (var p in tr.Packages)
                {
                    p.LiftM = 0;
                    if (_scalePlan.TryGetValue(p, out var scales) && scales.Count > 0)
                    {
                        StartWeighing(p, scales, t);
                        continue;
                    }
                    p.State = PackageState.Exited;
                    p.ExitTimeS = t;
                    _active.Remove(p);
                    Emit(SimEventType.PackageExited, EventSeverity.Info,
                        $"{p.Id} depositato nella stazione successiva (attraversamento {p.LeadTimeS:0.0} s)", Project.Transfer.Id, p.Id);
                }
                _scalePlan.Clear();
                tr.Packages.Clear();
                tr.Cycles++;
                SetTransferPhase(tr, TransferState.Returning, t);
                break;
            case TransferState.Returning:
                tr.LateralM = 0;
                SetTransferPhase(tr, TransferState.Home, t);
                break;
        }
    }

    private void SetTransferPhase(TransferRuntime tr, TransferState s, double t)
    {
        tr.State = s;
        tr.ElapsedS = 0;
        string txt = s switch
        {
            TransferState.Lifting => "sollevamento",
            TransferState.Translating => "traslazione",
            TransferState.Lowering => "abbassamento",
            TransferState.Depositing => "deposito",
            TransferState.Returning => "ritorno",
            _ => "in posizione"
        };
        string pk = tr.Packages.Count > 1 ? $" ({string.Join("+", tr.Packages.Select(p => p.Id))})" : "";
        Emit(SimEventType.TransferPhase, EventSeverity.Info, $"Trasferitore: {txt}{pk}", Project.Transfer.Id, tr.Package?.Id);
    }

    // ------------------------------------------------------------------ 9. Statistiche, registrazione, termine

    private void UpdateStatistics(double dt)
    {
        foreach (var z in _zones)
        {
            if (z.IsMoving)
            {
                z.RunningTimeS += dt;
                if (z.Mode == InverterMode.Run)
                {
                    if (z.Level == SpeedLevel.High) z.HighSpeedTimeS += dt; else z.LowSpeedTimeS += dt;
                }
            }
            bool occ = _active.Any(p => p.OnRollers && p.HeadM > z.Info.StartM + 1e-9 && p.TailM < z.Info.EndM - 1e-9);
            z.Occupied = occ;
            if (occ) z.OccupiedTimeS += dt;
            if (occ && z.BlockedByAccumulation) z.BlockedTimeS += dt;
            if (z.ReservedBy != null) z.ReservedTimeS += dt;
        }
        foreach (var pc in _photocells) if (pc.Output) pc.OccupiedTimeS += dt;

        int accumulated = 0;
        foreach (var p in _active)
        {
            if (p.State == PackageState.Accumulated) { accumulated++; p.WaitingTimeS += dt; }
            else if (p.State == PackageState.Aligned) p.WaitingTimeS += dt;
        }
        if (accumulated > MaxSimultaneousAccumulated) MaxSimultaneousAccumulated = accumulated;
    }

    private void InitRecorder()
    {
        foreach (var z in Topology.Zones) Recorder.Track("INV:" + z.Id, z.Inverter.Name.Length > 0 ? z.Inverter.Name : z.Id, TrackGroup.Inverter);
        foreach (var pc in Project.Photocells) Recorder.Track("PC:" + pc.Id, pc.Name.Length > 0 ? pc.Name : pc.Id, TrackGroup.Photocell);
        foreach (var s in Project.Strappers) Recorder.Track("STR:" + s.Id, s.Name.Length > 0 ? s.Name : s.Id, TrackGroup.Strapper);
        if (BundleClamp != null) Recorder.Track("KNOF", Label(Project.BundleClamp), TrackGroup.Clamp);
        if (Compactor != null) Recorder.Track("CMP", Label(Project.Compactor), TrackGroup.Clamp);
        if (Rejector != null) Recorder.Track("RJT", Label(Project.Rejector), TrackGroup.Rejector);
        Recorder.Track("STOPPER", Project.Stopper.Name.Length > 0 ? Project.Stopper.Name : "Stopper", TrackGroup.Stopper);
        foreach (var s in _stops) Recorder.Track("STOP:" + s.Id, s.Config.Name.Length > 0 ? s.Config.Name : s.Id, TrackGroup.Stop);
        Recorder.Track("TRANSFER", Project.Transfer.Name.Length > 0 ? Project.Transfer.Name : "Trasferitore", TrackGroup.Transfer);
        foreach (var s in _scales) Recorder.Track("SCALE:" + s.Id, Label(s.Config), TrackGroup.Scale);
        foreach (var z in Topology.Zones) Recorder.Track("ACC:" + z.Id, "Consenso ingresso " + z.Id, TrackGroup.Interlock);
    }

    private static string Label(ComponentBase c) => c.Name.Length > 0 ? c.Name : c.Id;

    private void RecordStates()
    {
        double t = Time;
        foreach (var z in _zones) Recorder.Set("INV:" + z.Id, t, z.CycleStateCode, z.Reason);
        foreach (var pc in _photocells) Recorder.Set("PC:" + pc.Id, t, pc.Output ? CycleState.On : CycleState.Off);
        foreach (var s in _strappers)
            Recorder.Set("STR:" + s.Id, t, s.ProcessState switch
            {
                StrapperProcessState.WaitingPackage => CycleState.Waiting,
                StrapperProcessState.Positioning => CycleState.Positioning,
                StrapperProcessState.PackageInPosition => CycleState.InPosition,
                StrapperProcessState.WaitingClampConsent => CycleState.WaitConsent,
                StrapperProcessState.StrappingCycle => s.State switch
                {
                    StrapperState.Preparing => CycleState.Preparing,
                    StrapperState.Returning => CycleState.Returning,
                    _ => CycleState.Working
                },
                StrapperProcessState.CycleCompleted => CycleState.Completed,
                StrapperProcessState.Unloading => CycleState.Unloading,
                StrapperProcessState.Fault => CycleState.Fault,
                _ => s.ArmState == StrapperArmState.Arming ? CycleState.Preparing : CycleState.Idle
            }, (s.ArmState == StrapperArmState.Arming && !s.Busy ? (s.IsSma ? "Riarmo" : "Carica filo") : ProcessLabel(s.ProcessState))
               + (s.Package != null ? " " + s.Package.Id : ""));
        if (BundleClamp != null) Recorder.Set("KNOF", t, ClampCycle(BundleClamp.State), Station?.Package?.Id ?? "");
        if (Compactor != null) Recorder.Set("CMP", t, ClampCycle(Compactor.State), Station?.Package?.Id ?? "");
        if (Rejector != null)
            Recorder.Set("RJT", t, Rejector.State switch
            {
                RejectorState.Enabled => CycleState.Consent,
                RejectorState.Positioning => CycleState.Positioning,
                RejectorState.InPosition => CycleState.InPosition,
                RejectorState.Ejecting => CycleState.Ejecting,
                RejectorState.Returning => CycleState.Returning,
                RejectorState.RestartConsent => CycleState.Waiting,
                RejectorState.Fault => CycleState.Fault,
                _ => CycleState.Idle
            }, Rejector.Package?.Id ?? "");
        Recorder.Set("STOPPER", t, Stopper.State switch
        {
            StopperState.Aligning => CycleState.Aligning,
            StopperState.Aligned => CycleState.Aligned,
            _ => CycleState.Idle
        }, Stopper.Package?.Id ?? "");
        foreach (var s in _stops)
            Recorder.Set("STOP:" + s.Id, t, s.State switch
            {
                StopState.Assigned => CycleState.Waiting,
                StopState.Positioning => CycleState.Positioning,
                StopState.Returning => CycleState.Positioning,
                StopState.Aligning => CycleState.Aligning,
                StopState.Aligned => CycleState.Aligned,
                _ => CycleState.Idle
            }, (s.Package?.Id ?? "") + (s.Reference == StopReference.Tail ? " (coda)" : " (testa)"));
        Recorder.Set("TRANSFER", t, Transfer.State switch
        {
            TransferState.Lifting => CycleState.Lifting,
            TransferState.Translating => CycleState.Translating,
            TransferState.Lowering => CycleState.Lowering,
            TransferState.Depositing => CycleState.Depositing,
            TransferState.Returning => CycleState.Returning,
            TransferState.WaitingDestination => CycleState.Interlock,
            _ => CycleState.Idle
        }, string.Join("+", Transfer.Packages.Select(p => p.Id)));
        foreach (var s in _scales)
            Recorder.Set("SCALE:" + s.Id, t, s.State switch
            {
                ScaleState.Directed => CycleState.Waiting,
                ScaleState.Positioning => CycleState.Positioning,
                ScaleState.PositionCheck => CycleState.InPosition,
                ScaleState.Stabilizing => CycleState.Stabilizing,
                ScaleState.Acquiring => CycleState.Acquiring,
                ScaleState.Completed => CycleState.Completed,
                ScaleState.Releasing => CycleState.Unloading,
                ScaleState.Fault => CycleState.Fault,
                _ => CycleState.Idle
            }, s.Package?.Id ?? "");
        foreach (var z in _zones) Recorder.Set("ACC:" + z.Id, t, z.Accepting ? CycleState.Consent : CycleState.Interlock,
            z.ReservedBy != null ? "Riservata a " + z.ReservedBy.Id : z.AutoStopped ? "Arresto automatico" : z.Running ? "" : "Zona ferma");
    }

    private static CycleState ClampCycle(ClampState s) => s switch
    {
        ClampState.Closing => CycleState.Closing,
        ClampState.Closed => CycleState.Closed,
        ClampState.Opening => CycleState.Opening,
        ClampState.Fault => CycleState.Fault,
        _ => CycleState.Idle
    };

    public static string ProcessLabel(StrapperProcessState s) => s switch
    {
        StrapperProcessState.Available => "Disponibile",
        StrapperProcessState.WaitingPackage => "Attesa pacco",
        StrapperProcessState.Positioning => "Posizionamento",
        StrapperProcessState.PackageInPosition => "Pacco in posizione",
        StrapperProcessState.WaitingClampConsent => "Attesa consenso stringifascio",
        StrapperProcessState.StrappingCycle => "Ciclo di legatura",
        StrapperProcessState.CycleCompleted => "Ciclo completato",
        StrapperProcessState.Unloading => "Scarico pacco",
        StrapperProcessState.Fault => "Guasto/blocco",
        _ => s.ToString()
    };

    private void CaptureFrameToRecorder() => Recorder.Frames.Add(CaptureFrame());

    private void CheckTermination(double t)
    {
        if (IsFinished) return;
        bool allIn = _nextIndex >= Project.Infeed.PackageCount;
        bool devicesIdle = !_strappers.Any(s => s.Busy) && (Rejector == null || !Rejector.Busy) && !_scales.Any(s => s.Busy)
                           && (BundleClamp == null || BundleClamp.IsOpen) && (Compactor == null || Compactor.IsOpen);
        // Fine: tutti i pacchi usciti/rigettati E macchine tornate a riposo (ultimo ritorno del trasferitore incluso).
        if (allIn && _active.Count == 0 && Transfer.State == TransferState.Home && devicesIdle)
        {
            string rej = RejectedPackages > 0 ? $", {RejectedPackages} rigettati" : "";
            Emit(SimEventType.Completed, EventSeverity.Info, $"Simulazione completata: {_packages.Count - RejectedPackages} pacchi usciti{rej}.");
            Finish("Completata", true);
            return;
        }
        bool busy = _strappers.Any(s => s.Busy) || Transfer.State != TransferState.Home || Stopper.State == StopperState.Aligning
                    || (Rejector != null && Rejector.Busy && Rejector.State != RejectorState.Enabled) || _scales.Any(s => s.Busy)
                    || (BundleClamp != null && !BundleClamp.IsOpen && !BundleClamp.IsClosed)
                    || (Compactor != null && !Compactor.IsOpen && !Compactor.IsClosed)
                    || (Station != null && Station.State is StationState.Compacting or StationState.Clamping or StationState.Unclamping or StationState.WaitingStrappers)
                    || _strappers.Any(s => s.ArmState == StrapperArmState.Arming)
                    || _stops.Any(s => s.State is StopState.Aligning or StopState.Returning) || _batchWaitSince != null;
        if (busy) _lastProgressTime = t;
        if (_active.Count > 0 && t - _lastProgressTime > Project.Simulation.DeadlockTimeoutS)
        {
            Deadlocked = true;
            var stopped = string.Join("; ", _zones.Where(z => !z.Running).Select(z => $"{z.Id}: {z.Reason}"));
            Emit(SimEventType.Deadlock, EventSeverity.Error,
                $"BLOCCO LINEA (deadlock): nessun movimento da {Project.Simulation.DeadlockTimeoutS:0} s. Zone ferme: {stopped}");
            Finish("Blocco linea (deadlock)", false);
            return;
        }
        if (t >= Project.Simulation.MaxSimulationTimeS - 1e-9)
        {
            Emit(SimEventType.TimeLimit, EventSeverity.Warning, "Raggiunta la durata massima di simulazione prima del completamento.");
            Finish("Durata massima raggiunta", false);
        }
    }

    private void Finish(string reason, bool ok)
    {
        if (IsFinished) return;
        IsFinished = true;
        CompletedNormally = ok;
        FinishReason = reason;
        Recorder.CloseAll(Time);
        if (Recorder.Frames.Count == 0 || Recorder.Frames[^1].TimeS < Time - 1e-9) CaptureFrameToRecorder();
    }

    private void Emit(SimEventType type, EventSeverity sev, string msg, string? comp = null, string? pkg = null)
        => _events.Add(new SimEvent(++_eventSeq, Time, type, sev, msg, comp, pkg));

    // ------------------------------------------------------------------ ILogicContext

    bool ILogicContext.PhotocellOccupied(string id) => _pcById.TryGetValue(id, out var pc) && pc.Output;
    bool ILogicContext.InverterMoving(string id) => _zoneById.TryGetValue(id, out var z) && z.IsMoving;
    bool ILogicContext.ZoneOccupied(string id) => _zoneById.TryGetValue(id, out var z) && z.Occupied;
    bool ILogicContext.StrapperBusy(string id) => _strappers.Any(s => s.Id == id && s.Busy);
    bool ILogicContext.StopperOccupied => Stopper.Package != null;
    bool ILogicContext.TransferAtHome => Transfer.State == TransferState.Home;
    bool ILogicContext.Signal(string name) => _signals.Contains(name);

    bool ILogicContext.AnyHeadInRange(double fromMm, double toMm)
    {
        double a = fromMm / 1000.0, b = toMm / 1000.0;
        return _active.Any(p => p.OnRollers && p.HeadM >= a && p.HeadM <= b);
    }

    bool ILogicContext.GapAheadLessThan(string inverterId, double gapMm)
    {
        if (!_zoneById.TryGetValue(inverterId, out var z)) return false;
        double g = gapMm / 1000.0;
        PackageRuntime? ahead = null;
        foreach (var p in _active)
        {
            if (!p.OnRollers) { ahead = null; continue; }
            if (ahead != null && p.HeadM >= z.Info.StartM && p.HeadM < z.Info.EndM && ahead.TailM - p.HeadM < g) return true;
            ahead = p;
        }
        return false;
    }

    bool ILogicContext.DeviceState(string deviceId, string state)
    {
        // Stato di carica della macchina di legatura: DEV(L1,Armed) / DEV(L1,Arming) / DEV(L1,Unarmed)
        var str = _strappers.FirstOrDefault(x => x.Id == deviceId);
        if (str != null && Enum.TryParse<StrapperArmState>(state?.Trim(), true, out var arm)) return str.ArmState == arm;
        var info = DeviceStatus(deviceId);
        if (info == null) return false;
        return string.IsNullOrWhiteSpace(state) ? info.Value.Busy : string.Equals(info.Value.State, state.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "t={0:0.000}s pacchi={1}", Time, _active.Count);
}
