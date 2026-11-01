using System.Globalization;
using LineSimulator.Core;
using LineSimulator.Engine.Logic;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;
using LineSimulator.Optimization;

namespace LineSimulator.Engine;

/// <summary>
/// Motore di simulazione a tempo discreto (passo fisso), deterministico e indipendente dalla UI.
///
/// Ciclo di scansione (come un PLC):
///  1. ingresso pacchi;  2. fotocellule (con tempo di risposta);  3. prenotazione zone a pacco singolo;
///  4. regole logiche (ingressi della scansione precedente);  5. comandi zone da valle a monte
///  (accumulo, legatura, allineamento, interblocchi);  6. rampe inverter;  7. moto pacchi
///  (trascinamento dei rulli con limite d'attrito, battuta stopper, contatti e distanze);
///  8. macchine (stazione di legatura, legatrici, stopper, trasferitore);  9. statistiche e registrazione.
///
/// Nessuna sorgente casuale: a parità di progetto i risultati sono identici.
/// </summary>
public sealed class SimulationEngine : ILogicContext
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

    public SimulationEngine(LineProject project)
    {
        Project = project;
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
            foreach (var s in z.Sections)
                _sections.Add((s.StartMm / 1000.0, s.EndMm / 1000.0, zr, s.IsEnabled));
        }
        _sections.Sort((a, b) => a.Start.CompareTo(b.Start));

        foreach (var pc in project.Photocells)
        {
            var r = new PhotocellRuntime(pc);
            _photocells.Add(r);
            _pcById[pc.Id] = r;
        }
        for (int i = 0; i < project.Strappers.Count; i++)
            _strappers.Add(new StrapperRuntime(project.Strappers[i], i));

        Stopper = new StopperRuntime(project.Stopper);
        Transfer = new TransferRuntime(project.Transfer);

        if (Topology.StrappingZone != null && project.StrappingStation.IsEnabled)
        {
            Station = new StationRuntime(_zoneById[Topology.StrappingZone.Id], project.StrappingStation.PositioningSpeed,
                project.StrappingStation.PositioningToleranceMm / 1000.0);
        }

        foreach (var id in project.Infeed.TypeSequence)
        {
            var t = project.PackageTypes.FirstOrDefault(x => x.Id == id);
            if (t != null) _typeSequence.Add(t);
        }
        if (_typeSequence.Count == 0 && project.PackageTypes.Count > 0) _typeSequence.Add(project.PackageTypes[0]);

        InitRecorder();

        if (Validation.HasErrors)
        {
            foreach (var e in Validation.Errors)
                Emit(SimEventType.ConfigurationError, EventSeverity.Error, e.Message, e.ComponentId);
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
        EvaluateLogic();
        ComputeZoneCommands(t0, dt);
        foreach (var z in _zones) z.UpdateDrive(dt, Stopper.Config.AlignmentRollerSpeed);
        MovePackages(t0, dt);

        _step++;
        double t1 = Time;
        UpdateStation(t1, dt);
        UpdateStrappers(t1, dt);
        UpdateStopper(t1, dt);
        UpdateTransfer(t1, dt);
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
        TransferLateralM = Transfer.LateralM
    };

    public ZoneRuntime? Zone(string id) => _zoneById.TryGetValue(id, out var z) ? z : null;

    // ------------------------------------------------------------------ 1. Ingresso

    private void Infeed(double t)
    {
        var cfg = Project.Infeed;
        if (_nextIndex >= cfg.PackageCount || _typeSequence.Count == 0) return;
        double due = cfg.FirstEntryTimeS + _nextIndex * cfg.IntervalS;
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
        pkg.StrapStatus = Station != null && pkg.StrapsTotal > 0 ? StrapStatus.Pending : StrapStatus.NotRequired;
        pkg.Velocity = DriveSpeed(pkg);
        _nextIndex++;
        _packages.Add(pkg);
        _active.Add(pkg);
        _lastProgressTime = t;
        Emit(SimEventType.PackageEntered, EventSeverity.Info, $"Ingresso {pkg.Id} ({type.Id}, {type.LengthMm:0} mm, {type.WeightKg:0} kg)", null, pkg.Id);
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
            if (pc.Update(t, found))
            {
                if (pc.Output) pc.Activations++;
                Emit(SimEventType.PhotocellChanged, EventSeverity.Trace,
                    $"{pc.Id} {(pc.Output ? "OCCUPATA" : "LIBERA")}", pc.Id, pc.RawPackage?.Id ?? pc.LastPackage?.Id);
            }
        }
    }

    // ------------------------------------------------------------------ 3. Prenotazioni zone a pacco singolo

    private void UpdateReservations(double t)
    {
        foreach (var z in _zones.Where(z => z.IsSinglePackage))
        {
            var res = z.ReservedBy;
            if (res != null)
            {
                bool release = z.Info.Kind == ZoneKind.Stopper
                    ? !res.OnRollers && (!Transfer.Config.BlockInfeedUntilHome || Transfer.State == TransferState.Home)
                    : !res.OnRollers || res.TailM >= z.Info.EndM - 1e-9;
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
                var p = _active.FirstOrDefault(x => x.OnRollers && x.HeadM > z.Info.StartM + 1e-9 && x.TailM < z.Info.EndM - 1e-9);
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
                    if (other == z.ReservedBy || !other.OnRollers) continue;
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

    private void StationAccept(PackageRuntime p, double t)
    {
        var st = Station!;
        st.Package = p;
        st.StopIndex = 0;
        st.Plan = null;
        if (p.StrapStatus != StrapStatus.Pending)
        {
            st.State = StationState.Releasing;
            return;
        }
        if (!_plans.TryGetValue(p.Type.Id, out var cmp) || !cmp.Selected.IsFeasible)
        {
            p.StrapStatus = StrapStatus.Failed;
            st.State = StationState.Releasing;
            Emit(SimEventType.StrappingPlanError, EventSeverity.Error,
                $"{p.Id} transita NON legato: piano di legatura non disponibile per {p.Type.Id}.", st.Zone.Id, p.Id);
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
            if (!_requests.TryGetValue(pc.Config.ControlledInverterId, out var req)) continue;
            switch (pc.Config.Action)
            {
                case PhotocellAction.SlowDown: req.Low = true; req.SpeedReasons.Add($"Rallentamento {pc.Id}"); break;
                case PhotocellAction.SpeedUp: req.High = true; req.SpeedReasons.Add($"Alta {pc.Id}"); break;
                case PhotocellAction.Stop: req.Stop = true; req.StopReasons.Add($"Arresto {pc.Id}"); break;
            }
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
            InverterMode mode;
            string reason;
            z.BlockedByAccumulation = false;

            if (!z.Config.IsEnabled)
            {
                mode = InverterMode.Stop; reason = "Inverter disabilitato";
            }
            else if (Station != null && z == Station.Zone && Station.State == StationState.Positioning)
            {
                mode = InverterMode.Position; reason = $"Posizionamento fermata {Station.StopIndex + 1}";
                z.PositionCommand = Station.VelocityCommand(dt);
            }
            else if (Station != null && z == Station.Zone && Station.State == StationState.Strapping)
            {
                mode = InverterMode.Stop; reason = "Legatura in corso";
            }
            else if (z.Info.Kind == ZoneKind.Stopper && Stopper.Package != null)
            {
                mode = InverterMode.Align; reason = Stopper.State == StopperState.Aligned ? "Allineato, attesa trasferitore" : "Allineamento allo stopper";
            }
            else
            {
                mode = InverterMode.Run; reason = "Marcia";
                foreach (var pc in _photocells)
                {
                    if (pc.Config.Action != PhotocellAction.Accumulation || pc.Config.ControlledInverterId != z.Id || !pc.Active) continue;
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

            z.Mode = mode;
            z.Reason = reason;
            if (mode == InverterMode.Run)
            {
                z.Level = req.Low ? SpeedLevel.Low : req.High ? SpeedLevel.High : z.Config.DefaultSpeed;
                if (req.SpeedReasons.Count > 0) z.Reason += " (" + string.Join(", ", req.SpeedReasons) + ")";
            }
            z.Accepting = z.AcceptsFor(null);

            if (mode != prevMode || (mode == InverterMode.Run && z.Level != prevLevel) || (mode == InverterMode.Stop && reason != prevReason))
            {
                string what = mode switch
                {
                    InverterMode.Run => z.Level == SpeedLevel.High ? "MARCIA ALTA" : "MARCIA BASSA",
                    InverterMode.Stop => "ARRESTO",
                    InverterMode.Position => "POSIZIONAMENTO",
                    _ => "ALLINEAMENTO"
                };
                Emit(SimEventType.InverterCommand, EventSeverity.Trace, $"{z.Id}: {what} - {z.Reason}", z.Id);
            }
        }
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
        return any ? v : 0;
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
            double vNew = Math.Clamp(vTarget, vOld - dvMax, vOld + dvMax);
            // In battuta allo stopper i rulli slittano volutamente sotto il pacco: non è un'anomalia.
            bool slipping = !p.InStopperContact && Math.Abs(vTarget - vNew) > 1e-9;
            if (slipping && !p.IsSlipping && Math.Abs(vTarget - vOld) > 0.01)
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
            if (transferInLine && p != Transfer.Package && newHead > Transfer.StartM && p.TailM < Transfer.EndM)
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

    // ------------------------------------------------------------------ 8. Macchine

    private void UpdateStation(double t, double dt)
    {
        var st = Station;
        if (st == null || st.Package == null) return;
        var p = st.Package;
        if (st.State != StationState.Idle && st.Zone.ReservedBy == p)
        {
            if (st.State == StationState.Releasing && !st.Zone.Running) st.BlockedTimeS += dt;
            else st.BusyTimeS += dt;
            p.StrappingStationTimeS += dt;
        }

        if (st.State == StationState.Positioning)
        {
            double r = st.TargetHeadM - p.HeadM;
            bool arrived = r <= 2e-4 && r >= -st.ToleranceM;
            bool overshoot = r < -st.ToleranceM;
            if (arrived || (overshoot && p.IsStopped))
            {
                double err = Math.Abs(r);
                if (err > st.MaxPositioningErrorM) st.MaxPositioningErrorM = err;
                if (overshoot)
                {
                    PositioningErrors++;
                    Emit(SimEventType.PositioningError, EventSeverity.Error,
                        $"{p.Id} oltre la fermata di {err * 1000:0.0} mm (tolleranza {st.ToleranceM * 1000:0.0} mm).", st.Zone.Id, p.Id);
                }
                else
                {
                    p.HeadM = st.TargetHeadM; // posizionamento a encoder: errore residuo < 0.2 mm
                }
                p.Velocity = 0;
                st.Zone.ActualSpeed = 0;
                st.Zone.PositionCommand = 0;
                StartStrappingAtStop(st, p, t);
            }
        }
        else if (st.State == StationState.Strapping)
        {
            if (st.ActiveStrappers.All(s => !s.Busy))
            {
                var stop = st.Plan!.Stops[st.StopIndex];
                p.StrapsDone += stop.Tasks.Count;
                foreach (var s in st.ActiveStrappers) s.ClearPackage();
                st.ActiveStrappers.Clear();
                st.StopIndex++;
                if (st.StopIndex < st.Plan.Stops.Count)
                {
                    st.State = StationState.Positioning;
                    st.TargetHeadM = st.Plan.Stops[st.StopIndex].HeadPositionM;
                    p.State = PackageState.Positioning;
                    Emit(SimEventType.PositioningStarted, EventSeverity.Info,
                        $"{p.Id} verso fermata {st.StopIndex + 1}/{st.Plan.Stops.Count} (testa a {st.TargetHeadM * 1000:0} mm)", st.Zone.Id, p.Id);
                }
                else
                {
                    st.State = StationState.Releasing;
                    p.StrapStatus = StrapStatus.Done;
                    p.State = PackageState.Transport;
                    Emit(SimEventType.StrappingCompleted, EventSeverity.Info,
                        $"{p.Id} legato: {p.StrapsDone}/{p.StrapsTotal} legature in {st.Plan.Stops.Count} fermate.", st.Zone.Id, p.Id);
                }
            }
        }
    }

    private void StartStrappingAtStop(StationRuntime st, PackageRuntime p, double t)
    {
        var stop = st.Plan!.Stops[st.StopIndex];
        st.State = StationState.Strapping;
        p.State = PackageState.Strapping;
        Emit(SimEventType.PositioningCompleted, EventSeverity.Info,
            $"{p.Id} in fermata {st.StopIndex + 1}: {string.Join(", ", stop.Tasks.Select(x => $"{x.StrapperId}→#{x.StrapIndex + 1}"))}", st.Zone.Id, p.Id);
        foreach (var task in stop.Tasks)
        {
            var s = _strappers.First(x => x.Id == task.StrapperId);
            if (s.Busy)
            {
                Emit(SimEventType.StrappingPlanError, EventSeverity.Error, $"{s.Id} occupata: impossibile avviare la legatura.", s.Id, p.Id);
                continue;
            }
            s.Start(p, task.StrapIndex);
            st.ActiveStrappers.Add(s);
            Emit(SimEventType.StrapperPhase, EventSeverity.Info, $"{s.Id}: preparazione legatura #{task.StrapIndex + 1} su {p.Id}", s.Id, p.Id);
        }
    }

    private void UpdateStrappers(double t, double dt)
    {
        foreach (var s in _strappers)
        {
            var changed = s.Advance(dt);
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

    private void UpdateTransfer(double t, double dt)
    {
        var tr = Transfer;
        var cfg = tr.Config;
        if (tr.State == TransferState.Home)
        {
            tr.LiftM = 0; tr.LateralM = 0;
            if (Stopper.State == StopperState.Aligned && Stopper.Package != null)
            {
                tr.Package = Stopper.Package;
                SetTransferPhase(tr, TransferState.Lifting, t);
            }
            return;
        }

        tr.BusyTimeS += dt;
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
        if (tr.Package != null && tr.State != TransferState.Returning)
        {
            tr.Package.LiftM = tr.State == TransferState.Depositing ? 0 : tr.LiftM;
            tr.Package.LateralM = tr.LateralM;
        }
        if (tr.ElapsedS < dur - 1e-9) return;

        switch (tr.State)
        {
            case TransferState.Lifting:
                if (tr.Package != null)
                {
                    tr.Package.State = PackageState.OnTransfer;
                    tr.Package.InStopperContact = false;
                    tr.Package.Velocity = 0;
                }
                Stopper.Package = null;
                Stopper.State = StopperState.Free;
                SetTransferPhase(tr, TransferState.Translating, t);
                break;
            case TransferState.Translating:
                SetTransferPhase(tr, TransferState.Lowering, t);
                break;
            case TransferState.Lowering:
                SetTransferPhase(tr, TransferState.Depositing, t);
                break;
            case TransferState.Depositing:
                if (tr.Package != null)
                {
                    var p = tr.Package;
                    p.State = PackageState.Exited;
                    p.ExitTimeS = t;
                    p.LiftM = 0;
                    _active.Remove(p);
                    Emit(SimEventType.PackageExited, EventSeverity.Info,
                        $"{p.Id} depositato nella stazione successiva (attraversamento {p.LeadTimeS:0.0} s)", Project.Transfer.Id, p.Id);
                }
                tr.Package = null;
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
        Emit(SimEventType.TransferPhase, EventSeverity.Info, $"Trasferitore: {txt}", Project.Transfer.Id, tr.Package?.Id);
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
        Recorder.Track("STOPPER", Project.Stopper.Name.Length > 0 ? Project.Stopper.Name : "Stopper", TrackGroup.Stopper);
        Recorder.Track("TRANSFER", Project.Transfer.Name.Length > 0 ? Project.Transfer.Name : "Trasferitore", TrackGroup.Transfer);
        foreach (var z in Topology.Zones) Recorder.Track("ACC:" + z.Id, "Consenso ingresso " + z.Id, TrackGroup.Interlock);
    }

    private void RecordStates()
    {
        double t = Time;
        foreach (var z in _zones) Recorder.Set("INV:" + z.Id, t, z.CycleStateCode, z.Reason);
        foreach (var pc in _photocells) Recorder.Set("PC:" + pc.Id, t, pc.Output ? CycleState.On : CycleState.Off);
        foreach (var s in _strappers)
            Recorder.Set("STR:" + s.Id, t, s.State switch
            {
                StrapperState.Preparing => CycleState.Preparing,
                StrapperState.Strapping => CycleState.Working,
                StrapperState.Returning => CycleState.Returning,
                _ => CycleState.Idle
            }, s.Package?.Id ?? "");
        Recorder.Set("STOPPER", t, Stopper.State switch
        {
            StopperState.Aligning => CycleState.Aligning,
            StopperState.Aligned => CycleState.Aligned,
            _ => CycleState.Idle
        }, Stopper.Package?.Id ?? "");
        Recorder.Set("TRANSFER", t, Transfer.State switch
        {
            TransferState.Lifting => CycleState.Lifting,
            TransferState.Translating => CycleState.Translating,
            TransferState.Lowering => CycleState.Lowering,
            TransferState.Depositing => CycleState.Depositing,
            TransferState.Returning => CycleState.Returning,
            _ => CycleState.Idle
        }, Transfer.Package?.Id ?? "");
        foreach (var z in _zones) Recorder.Set("ACC:" + z.Id, t, z.Accepting ? CycleState.Consent : CycleState.Interlock,
            z.ReservedBy != null ? "Riservata a " + z.ReservedBy.Id : z.Running ? "" : "Zona ferma");
    }

    private void CaptureFrameToRecorder() => Recorder.Frames.Add(CaptureFrame());

    private void CheckTermination(double t)
    {
        if (IsFinished) return;
        bool allIn = _nextIndex >= Project.Infeed.PackageCount;
        // Fine: tutti i pacchi usciti E macchine tornate a riposo (ultimo ritorno del trasferitore incluso nel ciclo).
        if (allIn && _active.Count == 0 && Transfer.State == TransferState.Home && !_strappers.Any(s => s.Busy))
        {
            Emit(SimEventType.Completed, EventSeverity.Info, $"Simulazione completata: {_packages.Count} pacchi usciti.");
            Finish("Completata", true);
            return;
        }
        bool busy = _strappers.Any(s => s.Busy) || Transfer.State != TransferState.Home || Stopper.State == StopperState.Aligning;
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

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "t={0:0.000}s pacchi={1}", Time, _active.Count);
}
