using LineSimulator.Core;
using LineSimulator.Engine.Logic;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Engine;

/// <summary>
/// Sequenze dei dispositivi di processo: stazione di legatura (KNCA 7/800 + stringifascio KNOF + compattatore),
/// rigettatrice SMA, fermi scomparenti con intestatura di testa/coda, trasferitore multi-pacco, bilici di pesatura.
/// Ogni sequenza avanza solo quando condizioni, consensi e conferme sono soddisfatti.
/// </summary>
public sealed partial class SimulationEngine
{
    private readonly List<StopRuntime> _stops = new();
    private readonly List<ScaleRuntime> _scales = new();
    /// <summary>Bilici integrati nella via a rulli: tempo di pesata in battuta prima del prelievo (0 = nessuno).</summary>
    private double _inlineWeighS;
    private readonly Dictionary<PackageRuntime, double> _inlineSince = new();
    private readonly Dictionary<PackageRuntime, List<ScaleRuntime>> _scalePlan = new();
    private readonly Dictionary<SequenceDeviceConfig, (LogicCondition? Consent, LogicCondition? Stop)> _deviceLogic = new();
    private PackageRuntime? _finalAssigned;
    private double? _batchWaitSince;

    public ClampRuntime? BundleClamp { get; private set; }
    /// <summary>
    /// Stringifascio comandati nell'ultima chiusura, uno per posizione (<see cref="LayoutRules.BundleClampPositions"/>):
    /// si chiude solo lo stringifascio che ha il pacco sotto, gli altri restano aperti.
    /// </summary>
    public IReadOnlyList<bool> BundleClampActive => _clampActive;
    private bool[] _clampActive = Array.Empty<bool>();
    private readonly Dictionary<string, int> _clampOfStrapper = new();
    public ClampRuntime? Compactor { get; private set; }
    public RejectorRuntime? Rejector { get; private set; }
    public IReadOnlyList<StopRuntime> IntermediateStops => _stops;
    /// <summary>Banking applicato (dalle impostazioni della simulazione; single se non c'è un fermo scomparente).</summary>
    public AccumulationMode EffectiveBanking { get; private set; } = AccumulationMode.SingleBanking;
    /// <summary>Intestatura applicata (dalle impostazioni della simulazione; testa se non c'è un fermo scomparente).</summary>
    public StopReference EffectiveHeading { get; private set; } = StopReference.Head;
    private readonly List<(EventSeverity, string)> _bankingNotes = new();
    private bool DoubleBanking => EffectiveBanking == AccumulationMode.DoubleBanking;
    /// <summary>Margine minimo dal pacco a valle durante l'extra corsa oltre il fermo (intestatura di coda).</summary>
    private const double TailOvershootClearanceM = 0.05;
    public IReadOnlyList<ScaleRuntime> Scales => _scales;
    public int RejectedPackages { get; private set; }
    public int WeighedPackages { get; private set; }
    public int DeviceAlarms { get; private set; }
    public int InterlockEvents { get; private set; }

    private void InitDevices()
    {
        var p = Project;
        if (Station != null && p.BundleClamp.IsEnabled)
        {
            BundleClamp = new ClampRuntime(p.BundleClamp, p.BundleClamp.CloseTimeS, p.BundleClamp.OpenTimeS, p.BundleClamp.HoldTimeS);
            _clampActive = new bool[LayoutRules.BundleClampPositions(p).Count];
            if (p.BundleClamp.PerMachine)
            {
                var machines = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).ToList();
                for (int i = 0; i < machines.Count; i++) _clampOfStrapper[machines[i].Id] = i;
            }
        }
        if (Station != null && p.Compactor.IsEnabled)
            Compactor = new ClampRuntime(p.Compactor, p.Compactor.CloseTimeS, p.Compactor.OpenTimeS, p.Compactor.HoldTimeS);
        if (p.Rejector.IsEnabled)
        {
            var rz = Topology.ZoneAt((p.Rejector.PositionMm + p.Rejector.LengthMm / 2) / 1000.0);
            IReadOnlyList<int> numbers;
            try { numbers = p.Rejector.ParseRejectNumbers(); } catch (FormatException) { numbers = Array.Empty<int>(); }
            if (rz != null) Rejector = new RejectorRuntime(p.Rejector, _zoneById[rz.Id], numbers.ToList());
        }
        // Banking e intestatura: quelli della tipologia simulata (tabella della schermata Pacchi; prima: sezione Simulazioni)
        var stopCfg = p.IntermediateStops.Where(s => s.IsEnabled).OrderByDescending(s => s.PositionMm).ToList();
        (EffectiveBanking, EffectiveHeading) = BankingOptions.ForSimulation(p);
        bool virtualSecond = false, virtualTail = false;
        if (stopCfg.Count == 0)
        {
            if (EffectiveHeading == StopReference.Tail && DoubleBanking)
            {
                _bankingNotes.Add((EventSeverity.Warning, "Intestatura di coda non possibile in double banking senza fermo scomparente: il secondo pacco si ferma a distanza dal primo, intestatura di testa."));
                EffectiveHeading = StopReference.Head;
            }
            // Double banking senza fermo: il secondo pacco si ferma per posizionamento (encoder) dietro al primo
            virtualSecond = DoubleBanking;
            // Single banking di coda senza fermo: il pacco si ferma per posizionamento con la coda a inizio trasferitore
            virtualTail = !DoubleBanking && EffectiveHeading == StopReference.Tail;
        }
        _bankingNotes.Insert(0, (EventSeverity.Info,
            $"Accumulo sul trasferitore: {(!DoubleBanking ? "single banking (1 pacco per ciclo)" : virtualSecond ? $"double banking (2 pacchi: il primo di testa sullo fermo finale, il secondo fermato per posizionamento a {p.Transfer.DoubleBankingGapMm:0} mm dal primo, senza fermo scomparente)" : "double banking (2 pacchi: il primo di testa sullo fermo finale, il secondo sul fermo scomparente)")}, " +
            $"intestatura di {(EffectiveHeading == StopReference.Head ? "testa" : virtualTail ? "coda (fermato per posizionamento con la coda a inizio trasferitore)" : "coda (oltre il fermo, il fermo si solleva e il pacco torna indietro)")}"));
        // Single banking di testa: i fermi scomparenti restano abbassati e il pacco va fino allo fermo finale
        if (virtualSecond)
            _stops.Add(new StopRuntime(new IntermediateStopConfig
            {
                Id = "POS2", Name = "Posizionamento secondo pacco (encoder)", AlignmentTimeS = 0,
                PositionToleranceMm = 3, PositionMm = p.Stopper.PositionMm
            }, StopReference.Head, isVirtual: true));
        else if (virtualTail)
            _stops.Add(new StopRuntime(new IntermediateStopConfig
            {
                Id = "POSC", Name = "Posizionamento di coda (encoder)", AlignmentTimeS = 0,
                PositionToleranceMm = 3, PositionMm = p.Transfer.PositionMm
            }, StopReference.Head, isVirtual: true) { TailPositioning = true });
        else if (DoubleBanking || EffectiveHeading == StopReference.Tail)
            foreach (var s in stopCfg) _stops.Add(new StopRuntime(s, EffectiveHeading));
        if (_stops.Count > 0 && Topology.StopperZone != null)
            _zoneById[Topology.StopperZone.Id].MultiStop = true;
        for (int i = 0; i < p.Scales.Count; i++)
            if (p.Scales[i].IsEnabled && p.Scales[i].Mounting == ScaleMounting.Pallet) _scales.Add(new ScaleRuntime(p.Scales[i], i));
        _inlineWeighS = p.Scales.Where(s => s.IsEnabled && s.Mounting == ScaleMounting.InLine).Select(s => s.AcquisitionTimeS).DefaultIfEmpty(0).Max();

        var devices = new List<SequenceDeviceConfig> { p.BundleClamp, p.Compactor, p.Rejector };
        devices.AddRange(p.Scales);
        foreach (var d in devices) _deviceLogic[d] = (ParseCondition(d.StartConsent), ParseCondition(d.StopCondition));
    }

    private static LogicCondition? ParseCondition(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return ConditionText.Parse(text); }
        catch (FormatException) { return new ConstantCondition { Value = false }; } // segnalato dal validatore
    }

    /// <summary>Consenso di avvio del dispositivo (vero se non configurato).</summary>
    private bool Consent(SequenceDeviceConfig cfg)
        => !_deviceLogic.TryGetValue(cfg, out var l) || l.Consent == null || LogicEvaluator.Evaluate(l.Consent, this);

    /// <summary>Condizione di arresto attiva: la sequenza del dispositivo resta sospesa.</summary>
    private bool StopActive(SequenceDeviceConfig cfg)
        => _deviceLogic.TryGetValue(cfg, out var l) && l.Stop != null && LogicEvaluator.Evaluate(l.Stop, this);

    private void Alarm(SimEventType type, string msg, string? comp, string? pkg)
    {
        DeviceAlarms++;
        Emit(type, EventSeverity.Error, msg, comp, pkg);
    }

    // ================================================================== Stazione di legatura (KNCA + KNOF + compattatore)

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

        switch (st.State)
        {
            case StationState.Positioning:
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
                    ArrivedAtStop(st, p, t);
                }
                break;
            }
            case StationState.Compacting:
            {
                var c = Compactor!;
                if (c.IsOpen) { TryCommandClose(c, Project.Compactor.PositionMm, st, p); break; }
                if (c.IsClosed)
                {
                    st.PhaseElapsedS += dt;
                    if (st.PhaseElapsedS >= c.HoldS - 1e-9)
                    {
                        Emit(SimEventType.CompactorPhase, EventSeverity.Info, $"{c.Id}: {p.Id} compattato, consenso alla legatura", c.Id, p.Id);
                        AfterCompaction(st, p, t);
                    }
                }
                break;
            }
            case StationState.WaitingStrappers:
                StartStrappingAtStop(st, p, t);
                break;
            case StationState.Clamping:
            {
                var k = BundleClamp!;
                if (k.IsOpen) { TryCommandClose(k, BundleClampPositionsMm(), st, p); break; }
                if (k.IsClosed) TryStartStrapping(st, p, t);
                break;
            }
            case StationState.Strapping:
            {
                if (BundleClamp == null && Compactor == null)
                {
                    if (st.ActiveStrappers.All(s => !s.Busy)) FinishStop(st, p);
                    break;
                }
                if (!st.StrappingPhaseDone && st.ActiveStrappers.All(s => !s.Faulted && s.State is StrapperState.Returning or StrapperState.Idle))
                {
                    st.StrappingPhaseDone = true;
                    st.PhaseElapsedS = 0;
                    if (BundleClamp != null)
                        Emit(SimEventType.BundleClampPhase, EventSeverity.Info, $"{BundleClamp.Id}: legatura terminata, mantenimento {BundleClamp.HoldS:0.##} s", BundleClamp.Id, p.Id);
                }
                if (st.StrappingPhaseDone)
                {
                    st.PhaseElapsedS += dt;
                    if (BundleClamp == null || st.PhaseElapsedS >= BundleClamp.HoldS - 1e-9) BeginUnclamp(st, p);
                }
                break;
            }
            case StationState.Unclamping:
            {
                if (Compactor != null && !st.CompactorOpenCommanded && (BundleClamp == null || BundleClamp.IsOpen))
                {
                    if (Compactor.CommandOpen())
                        Emit(SimEventType.CompactorPhase, EventSeverity.Info, $"{Compactor.Id}: comando di apertura", Compactor.Id, p.Id);
                    st.CompactorOpenCommanded = true;
                }
                bool clampOk = BundleClamp == null || BundleClamp.IsOpen;
                bool compOk = Compactor == null || Compactor.IsOpen;
                if (clampOk && compOk && st.ActiveStrappers.All(s => !s.Busy)) FinishStop(st, p);
                break;
            }
        }
    }

    private void ArrivedAtStop(StationRuntime st, PackageRuntime p, double t)
    {
        if (!p.EncoderReferenced && _photocells.Any(x => x.Config.Purpose == PhotocellPurpose.StrappingReference && x.Config.IsEnabled)
            && _reportedOnce.Add("noref:" + p.Id))
        {
            PositioningErrors++;
            Emit(SimEventType.PositioningError, EventSeverity.Error, $"{p.Id} in fermata senza riferimento encoder (fotocellula di riferimento non impegnata)", st.Zone.Id, p.Id);
        }
        st.PhaseElapsedS = 0;
        st.StrappingPhaseDone = false;
        st.CompactorOpenCommanded = false;
        st.ClampOpenCommanded = false;
        st.InterlockReported = false;
        if (Compactor != null)
        {
            st.State = StationState.Compacting;
            p.State = PackageState.Strapping;
            Emit(SimEventType.CompactorPhase, EventSeverity.Info, $"{p.Id} in posizione di compattazione, via a rulli ferma", Compactor.Id, p.Id);
            TryCommandClose(Compactor, Project.Compactor.PositionMm, st, p);
        }
        else if (BundleClamp != null)
        {
            st.State = StationState.Clamping;
            p.State = PackageState.Strapping;
            Emit(SimEventType.BundleClampPhase, EventSeverity.Info, $"{p.Id} in posizione di legatura", BundleClamp.Id, p.Id);
            TryCommandClose(BundleClamp, BundleClampPositionsMm(), st, p);
        }
        else
        {
            StartStrappingAtStop(st, p, t);
        }
    }

    private void AfterCompaction(StationRuntime st, PackageRuntime p, double t)
    {
        st.PhaseElapsedS = 0;
        st.InterlockReported = false;
        if (BundleClamp != null)
        {
            st.State = StationState.Clamping;
            TryCommandClose(BundleClamp, BundleClampPositionsMm(), st, p);
        }
        else TryStartStrapping(st, p, t);
    }

    /// <summary>Comando di chiusura con interblocchi: pacco presente sotto il dispositivo, via a rulli ferma, consenso.</summary>
    /// <summary>Posizioni degli stringifascio: uno per macchina (se previsto) oppure quello unico; si chiudono insieme.</summary>
    private IReadOnlyList<double> BundleClampPositionsMm() => LayoutRules.BundleClampPositions(Project).Select(x => x.PositionMm).ToList();

    private void TryCommandClose(ClampRuntime c, double positionMm, StationRuntime st, PackageRuntime p)
        => TryCommandClose(c, new[] { positionMm }, st, p);

    private void TryCommandClose(ClampRuntime c, IReadOnlyList<double> positionsMm, StationRuntime st, PackageRuntime p)
    {
        if (!c.IsOpen) return;
        var type = c == BundleClamp ? SimEventType.BundleClampPhase : SimEventType.CompactorPhase;
        // Si chiude solo lo stringifascio che ha il pacco sotto: con uno per macchina quelli senza pacco restano aperti
        var under = positionsMm.Select(mm => p.TailM <= mm / 1000.0 + 1e-6 && p.HeadM >= mm / 1000.0 - 1e-6).ToArray();
        bool covers = under.Any(x => x);
        double positionMm = positionsMm.Count > 0 ? positionsMm[0] : 0;
        if (!covers)
        {
            if (!st.InterlockReported)
            {
                st.InterlockReported = true;
                InterlockEvents++;
                Alarm(SimEventType.Interlock, $"{c.Id}: {p.Id} non si trova sotto il dispositivo ({positionMm:0} mm): chiusura non consentita", c.Id, p.Id);
            }
            return;
        }
        bool still = !st.Zone.IsMoving && p.IsStopped;
        bool consent = Consent(c.Config) && !StopActive(c.Config);
        if (!still || !consent)
        {
            if (!st.InterlockReported)
            {
                st.InterlockReported = true;
                InterlockEvents++;
                Emit(SimEventType.Interlock, EventSeverity.Info,
                    $"{c.Id}: chiusura in attesa ({(!still ? "via a rulli/pacco non fermi" : "consenso di avvio assente")})", c.Id, p.Id);
            }
            return;
        }
        if (c.CommandClose())
        {
            st.InterlockReported = false;
            string which = "";
            if (c == BundleClamp && under.Length == _clampActive.Length)
            {
                Array.Copy(under, _clampActive, under.Length);
                if (under.Length > 1)
                {
                    var ids = LayoutRules.BundleClampPositions(Project).Select(x => x.Id).ToList();
                    var open = ids.Where((_, i) => !under[i]).ToList();
                    which = $" ({string.Join(", ", ids.Where((_, i) => under[i]))}" + (open.Count > 0 ? $"; restano aperti {string.Join(", ", open)}: pacco non sotto)" : ")");
                }
            }
            Emit(type, EventSeverity.Info, $"{c.Id}: comando di chiusura su {p.Id}{which}", c.Id, p.Id);
        }
    }

    private void TryStartStrapping(StationRuntime st, PackageRuntime p, double t)
    {
        bool clampOk = BundleClamp == null || BundleClamp.IsClosed;
        var phase = Project.Compactor.OpenPhase;
        if (Compactor != null && phase == CompactorOpenPhase.AfterBundleClampClosed && BundleClamp != null && BundleClamp.IsClosed && !st.CompactorOpenCommanded)
        {
            if (Compactor.CommandOpen())
                Emit(SimEventType.CompactorPhase, EventSeverity.Info, $"{Compactor.Id}: apertura (fascio trattenuto dallo stringifascio)", Compactor.Id, p.Id);
            st.CompactorOpenCommanded = true;
        }
        bool compOk = Compactor == null || Compactor.IsClosed || st.CompactorOpenCommanded;
        if (!clampOk || !compOk) return;
        StartStrappingAtStop(st, p, t);
    }

    /// <summary>Avvio delle legatrici della fermata. Interblocco: con stringifascio presente serve la chiusura confermata.</summary>
    private bool StartStrappingAtStop(StationRuntime st, PackageRuntime p, double t)
    {
        if (BundleClamp != null && !BundleClamp.IsClosed)
        {
            InterlockEvents++;
            Emit(SimEventType.Interlock, EventSeverity.Error, $"Legatura NON avviata su {p.Id}: stringifascio {BundleClamp.Id} non chiuso/confermato", BundleClamp.Id, p.Id);
            return false;
        }
        var stop = st.Plan!.Stops[st.StopIndex];
        // Stringifascio per macchina: ogni macchina della fermata lega solo con il proprio stringifascio chiuso sul pacco
        if (BundleClamp != null && _clampOfStrapper.Count > 0)
        {
            var uncovered = stop.Tasks.Select(k => k.StrapperId).Distinct()
                .Where(id => _clampOfStrapper.TryGetValue(id, out int i) && i < _clampActive.Length && !_clampActive[i]).ToList();
            if (uncovered.Count > 0)
            {
                if (!st.InterlockReported)
                {
                    st.InterlockReported = true;
                    InterlockEvents++;
                    Alarm(SimEventType.Interlock, $"Legatura NON avviata su {p.Id}: lo stringifascio di {string.Join(", ", uncovered)} non ha il pacco sotto", BundleClamp.Id, p.Id);
                }
                return false;
            }
        }
        // Le macchine della fermata devono essere armate (carica del filo KNCA / riarmo SMA)
        var notArmed = stop.Tasks.Select(k => _strappers.First(x => x.Id == k.StrapperId)).Where(x => !x.Busy && !x.IsArmed).ToList();
        if (notArmed.Count > 0)
        {
            foreach (var x in notArmed)
                if (x.BeginArming())
                    Emit(SimEventType.StrapperPhase, EventSeverity.Info, $"{x.Id}: {(x.IsSma ? "riarmo" : "carica del filo")} (pacco {p.Id} in posizione)", x.Id, p.Id);
            if (st.State != StationState.WaitingStrappers)
            {
                st.State = StationState.WaitingStrappers;
                p.State = PackageState.Strapping;
                Emit(SimEventType.Interlock, EventSeverity.Info, $"{p.Id} in posizione: attesa {(notArmed.Any(x => !x.IsSma) ? "carica del filo" : "riarmo")} di {string.Join(", ", notArmed.Select(x => x.Id))}", st.Zone.Id, p.Id);
            }
            return false;
        }
        st.State = StationState.Strapping;
        st.StrappingPhaseDone = false;
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
            if (s.Faulted)
                Alarm(SimEventType.StrapperFault, $"{s.Id}: GUASTO/BLOCCO legatrice al ciclo {s.CyclesStarted} (ripristino in {s.Config.FaultResetTimeS:0.#} s)", s.Id, p.Id);
        }
        return true;
    }

    private void BeginUnclamp(StationRuntime st, PackageRuntime p)
    {
        st.State = StationState.Unclamping;
        if (BundleClamp != null && !st.ClampOpenCommanded)
        {
            if (BundleClamp.CommandOpen())
                Emit(SimEventType.BundleClampPhase, EventSeverity.Info, $"{BundleClamp.Id}: consenso all'apertura, comando di apertura", BundleClamp.Id, p.Id);
            st.ClampOpenCommanded = true;
        }
        if (Compactor != null && !st.CompactorOpenCommanded && Project.Compactor.OpenPhase == CompactorOpenPhase.AfterStrapping)
        {
            if (Compactor.CommandOpen())
                Emit(SimEventType.CompactorPhase, EventSeverity.Info, $"{Compactor.Id}: fine legatura, comando di apertura", Compactor.Id, p.Id);
            st.CompactorOpenCommanded = true;
        }
    }

    /// <summary>Fine lavorazione alla fermata: fermata successiva o rilascio del pacco.</summary>
    private void FinishStop(StationRuntime st, PackageRuntime p)
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

    private void UpdateClamps(double dt)
    {
        foreach (var (c, type) in new[] { (BundleClamp, SimEventType.BundleClampPhase), (Compactor, SimEventType.CompactorPhase) })
        {
            if (c == null) continue;
            if (!c.IsOpen) c.BusyTimeS += dt;
            var ev = c.Advance(dt, StopActive(c.Config));
            if (ev == null) continue;
            if (ev.Value.Alarm) Alarm(SimEventType.DeviceAlarm, $"{c.Id}: {ev.Value.Text}", c.Id, Station?.Package?.Id);
            else Emit(type, EventSeverity.Info, $"{c.Id}: {ev.Value.Text}", c.Id, Station?.Package?.Id);
        }
    }

    // ------------------------------------------------------------------ Stati di processo KNCA 7/800

    private void UpdateStrapperProcessStates(double t)
    {
        foreach (var s in _strappers)
        {
            var ns = ComputeProcessState(s);
            if (ns != s.ProcessState)
            {
                s.ProcessState = ns;
                s.ProcessStateSinceS = t;
                s.ConsentAlarmRaised = false;
                Emit(SimEventType.StrapperProcess, EventSeverity.Info, $"{s.Id} ({s.Config.Model}): {ProcessLabel(ns)}", s.Id, Station?.Package?.Id);
            }
            else if (ns == StrapperProcessState.WaitingClampConsent && !s.ConsentAlarmRaised && t - s.ProcessStateSinceS > s.Config.ConsentTimeoutS)
            {
                s.ConsentAlarmRaised = true;
                DeviceAlarms++;
                Emit(SimEventType.DeviceAlarm, EventSeverity.Warning,
                    $"{s.Id}: attesa consenso stringifascio oltre {s.Config.ConsentTimeoutS:0} s", s.Id, Station?.Package?.Id);
            }
        }
    }

    private StrapperProcessState ComputeProcessState(StrapperRuntime s)
    {
        if (!s.Config.IsEnabled) return StrapperProcessState.Available;
        if (s.Faulted) return StrapperProcessState.Fault;
        if (s.Busy) return StrapperProcessState.StrappingCycle;
        var st = Station;
        if (st == null) return StrapperProcessState.Available;
        var p = st.Package;
        if (p == null || st.State == StationState.Idle)
        {
            bool incoming = (_stationUpstream != null && _stationUpstream.Occupied) || st.Zone.Occupied;
            return incoming ? StrapperProcessState.WaitingPackage : StrapperProcessState.Available;
        }
        var plan = st.Plan;
        bool assignedNow = plan != null && st.StopIndex < plan.Stops.Count && plan.Stops[st.StopIndex].Tasks.Any(x => x.StrapperId == s.Id);
        bool hasLater = plan != null && plan.Stops.Skip(st.StopIndex + 1).Any(x => x.Tasks.Any(k => k.StrapperId == s.Id));
        bool worked = s.LastPackage == p;
        if (st.State == StationState.Releasing) return worked ? StrapperProcessState.Unloading : StrapperProcessState.Available;
        if (assignedNow)
        {
            return st.State switch
            {
                StationState.Positioning => StrapperProcessState.Positioning,
                StationState.Compacting or StationState.WaitingStrappers => StrapperProcessState.PackageInPosition,
                StationState.Clamping => StrapperProcessState.WaitingClampConsent,
                _ => StrapperProcessState.CycleCompleted
            };
        }
        if (hasLater) return st.State == StationState.Positioning ? StrapperProcessState.Positioning : StrapperProcessState.WaitingPackage;
        return worked ? StrapperProcessState.CycleCompleted : StrapperProcessState.Available;
    }

    // ================================================================== Rigettatrice SMA

    /// <summary>Abilitazione del ciclo di rigetto e avvio del posizionamento (prima dei comandi zona).</summary>
    private void UpdateRejectorEnable(double t)
    {
        var r = Rejector;
        if (r == null) return;
        var z = r.Zone;
        if (r.State == RejectorState.Idle)
        {
            var p = _active.FirstOrDefault(x => x.OnRollers && x.RejectRequested && !x.RejectHandled);
            if (p != null)
            {
                r.Package = p;
                r.TargetHeadM = r.CenterM + p.LengthM / 2;
                r.State = RejectorState.Enabled;
                r.ElapsedS = 0;
                Emit(SimEventType.RejectorPhase, EventSeverity.Info,
                    $"{r.Id}: ciclo di rigetto abilitato per {p.Id} (posizione di rigetto: testa a {r.TargetHeadM * 1000:0} mm)", r.Id, p.Id);
                if (p.LengthM > r.EndM - r.StartM + 1e-6 && _reportedOnce.Add("smalen:" + p.Id))
                    Emit(SimEventType.DeviceAlarm, EventSeverity.Warning, $"{r.Id}: {p.Id} più lungo del campo di spinta", r.Id, p.Id);
            }
        }
        if (r.State == RejectorState.Enabled && r.Package != null)
        {
            var p = r.Package;
            if (!p.OnRollers) { r.State = RejectorState.Idle; r.Package = null; }
            else if (p.HeadM > z.Info.StartM + 1e-9)
            {
                bool clearAhead = !_active.Any(q => q != p && q.OnRollers && q.HeadM > p.HeadM && q.TailM < z.Info.EndM - 1e-9);
                if (p.HeadM > r.TargetHeadM + r.ToleranceM)
                {
                    p.RejectHandled = true;
                    r.State = RejectorState.Idle;
                    r.Package = null;
                    Alarm(SimEventType.DeviceAlarm, $"{r.Id}: {p.Id} oltre la posizione di rigetto, NON rigettato (transita)", r.Id, p.Id);
                }
                else if (clearAhead)
                {
                    r.State = RejectorState.Positioning;
                    p.State = PackageState.Positioning;
                    Emit(SimEventType.RejectorPhase, EventSeverity.Info, $"{r.Id}: arresto e posizionamento di {p.Id}", r.Id, p.Id);
                }
            }
        }
        // Il pacco da rigettare entra nella zona della rigettatrice solo quando la zona è libera da altri pacchi
        // (la zona è comandata da un unico inverter: il posizionamento muoverebbe anche gli altri pacchi).
        z.EntryBlockedFor = null;
        if (r.State == RejectorState.Enabled && r.Package != null && r.Package.HeadM <= z.Info.StartM + 1e-9
            && _active.Any(q => q != r.Package && q.OnRollers && q.HeadM > z.Info.StartM + 1e-9 && q.TailM < z.Info.EndM - 1e-9))
            z.EntryBlockedFor = r.Package;
        bool hold = r.State is RejectorState.Positioning or RejectorState.InPosition or RejectorState.Ejecting
            or RejectorState.Returning or RejectorState.RestartConsent or RejectorState.Fault;
        z.EntryHeld = hold;
        z.EntryHeldFor = hold && r.State == RejectorState.Positioning ? r.Package : null;
    }

    private void UpdateRejector(double t, double dt)
    {
        var r = Rejector;
        if (r == null || r.State == RejectorState.Idle) return;
        if (r.State != RejectorState.Enabled) r.BusyTimeS += dt;
        var p = r.Package;
        if (p == null) { r.State = RejectorState.Idle; return; }
        var cfg = r.Config;
        bool hold = StopActive(cfg);
        double sign = cfg.Side == Project.Transfer.DestinationSide ? 1 : -1;
        switch (r.State)
        {
            case RejectorState.Positioning:
            {
                double rem = r.TargetHeadM - p.HeadM;
                bool arrived = rem <= 2e-4 && rem >= -r.ToleranceM;
                bool overshoot = rem < -r.ToleranceM;
                if (arrived || (overshoot && p.IsStopped))
                {
                    if (overshoot)
                    {
                        PositioningErrors++;
                        Emit(SimEventType.PositioningError, EventSeverity.Error, $"{r.Id}: {p.Id} oltre la posizione di rigetto di {-rem * 1000:0.0} mm", r.Id, p.Id);
                    }
                    else p.HeadM = r.TargetHeadM;
                    p.Velocity = 0;
                    r.Zone.ActualSpeed = 0;
                    r.Zone.PositionCommand = 0;
                    r.State = RejectorState.InPosition;
                    r.ElapsedS = 0;
                    Emit(SimEventType.RejectorPhase, EventSeverity.Info, $"{r.Id}: {p.Id} fermo in posizione di rigetto", r.Id, p.Id);
                }
                break;
            }
            case RejectorState.InPosition:
            {
                bool still = !r.Zone.IsMoving && p.IsStopped;
                bool free = !_active.Any(q => q != p && q.OnRollers && q.HeadM > r.StartM && q.TailM < r.EndM);
                if (!hold && still && free && Consent(cfg))
                {
                    r.State = RejectorState.Ejecting;
                    r.ElapsedS = 0;
                    r.Cycles++;
                    r.FaultPending = cfg.SimulatedFaultAtCycle > 0 && r.Cycles == cfg.SimulatedFaultAtCycle;
                    p.State = PackageState.Rejecting;
                    p.Velocity = 0;
                    Emit(SimEventType.RejectorPhase, EventSeverity.Info, $"{r.Id}: comando di espulsione {p.Id}", r.Id, p.Id);
                }
                else if (_reportedOnce.Add($"smawait:{p.Id}"))
                {
                    InterlockEvents++;
                    Emit(SimEventType.Interlock, EventSeverity.Info,
                        $"{r.Id}: espulsione in attesa ({(!still ? "via a rulli in moto" : !free ? "altro pacco nel campo di spinta" : "consenso assente")})", r.Id, p.Id);
                }
                break;
            }
            case RejectorState.Ejecting:
            {
                if (hold) break;
                r.ElapsedS += dt;
                double f = cfg.EjectTimeS > 0 ? Math.Min(1, r.ElapsedS / cfg.EjectTimeS) : 1;
                r.StrokeM = r.StrokeMaxM * f;
                p.LateralM = sign * r.StrokeM;
                if (!r.FaultPending && r.ElapsedS >= cfg.EjectTimeS + cfg.ConfirmDelayS - 1e-9)
                {
                    Emit(SimEventType.RejectorPhase, EventSeverity.Info, $"{r.Id}: espulsione di {p.Id} confermata", r.Id, p.Id);
                    p.State = PackageState.Rejected;
                    p.RejectedTimeS = t;
                    p.RejectHandled = true;
                    _active.Remove(p);
                    RejectedPackages++;
                    r.Rejected++;
                    Emit(SimEventType.PackageRejected, EventSeverity.Info, $"{p.Id} rigettato fuori linea da {r.Id}", r.Id, p.Id);
                    r.State = RejectorState.Returning;
                    r.ElapsedS = 0;
                }
                else if (r.ElapsedS >= cfg.ConfirmTimeoutS - 1e-9)
                {
                    r.State = RejectorState.Fault;
                    r.ElapsedS = 0;
                    r.Faults++;
                    Alarm(SimEventType.DeviceAlarm, $"{r.Id}: conferma di espulsione non ricevuta entro {cfg.ConfirmTimeoutS:0.#} s (guasto)", r.Id, p.Id);
                }
                break;
            }
            case RejectorState.Fault:
                r.ElapsedS += dt;
                if (r.ElapsedS >= cfg.FaultResetTimeS - 1e-9)
                {
                    r.FaultPending = false;
                    r.State = RejectorState.Ejecting;
                    r.ElapsedS = cfg.EjectTimeS;
                    Emit(SimEventType.RejectorPhase, EventSeverity.Info, $"{r.Id}: guasto ripristinato", r.Id, p.Id);
                }
                break;
            case RejectorState.Returning:
                if (hold) break;
                r.ElapsedS += dt;
                r.StrokeM = r.StrokeMaxM * (cfg.ReturnTimeS > 0 ? Math.Max(0, 1 - r.ElapsedS / cfg.ReturnTimeS) : 0);
                if (r.ElapsedS >= cfg.ReturnTimeS - 1e-9)
                {
                    r.StrokeM = 0;
                    r.State = RejectorState.RestartConsent;
                    r.ElapsedS = 0;
                    Emit(SimEventType.RejectorPhase, EventSeverity.Info, $"{r.Id}: spintore tornato in posizione iniziale", r.Id, p.Id);
                }
                break;
            case RejectorState.RestartConsent:
                r.ElapsedS += dt;
                if (r.ElapsedS >= cfg.RestartDelayS - 1e-9)
                {
                    r.State = RejectorState.Idle;
                    r.Package = null;
                    r.ElapsedS = 0;
                    Emit(SimEventType.RejectorPhase, EventSeverity.Info, $"{r.Id}: consenso alla ripartenza della linea", r.Id, p.Id);
                }
                break;
        }
    }

    // ================================================================== Fermi scomparenti e accumulo multi-pacco

    private void UpdateMultiStopReservations(ZoneRuntime z)
    {
        z.Assigned.RemoveAll(p => !p.OnRollers);
        bool canAssign = !Transfer.Config.BlockInfeedUntilHome || Transfer.State == TransferState.Home;
        foreach (var p in _active)
        {
            if (!p.OnRollers || z.Assigned.Contains(p)) continue;
            if (!(p.HeadM > z.Info.StartM + 1e-9 && p.TailM < z.Info.EndM - 1e-9)) continue;
            int stop = canAssign ? NextFreeStopFor(p) : -1;
            if (stop >= 0) AssignStop(p, stop, z);
            else if (_reportedOnce.Add($"overfill:{z.Id}:{p.Id}"))
            {
                ZoneOverfills++;
                Emit(SimEventType.ZoneOverfilled, EventSeverity.Warning,
                    $"{p.Id} entrato nella zona {z.Id} senza un fermo libero e compatibile: verificare accumulo e fermi.", z.Id, p.Id);
            }
        }
        z.ReservedBy = z.Assigned.Count > 0 ? z.Assigned[0] : null;
        var next = NextCandidate(z);
        z.MultiCanAcceptNew = canAssign && (next != null ? NextFreeStopFor(next) >= 0 : FirstFreeStop() >= 0);
    }

    /// <summary>Primo pacco a monte non ancora assegnato che può arrivare ai fermi.</summary>
    private PackageRuntime? NextCandidate(ZoneRuntime z)
    {
        foreach (var p in _active)
        {
            if (!p.OnRollers || p.AssignedStop >= 0 || z.Assigned.Contains(p)) continue;
            if (p.RejectRequested && !p.RejectHandled && Rejector != null && Rejector.Zone.Info.Index < z.Info.Index) continue;
            return p;
        }
        return null;
    }

    /// <summary>Primo fermo libero nell'ordine fermo finale → fermi scomparenti da valle a monte (-1 se nessuno).</summary>
    private int FirstFreeStop()
    {
        if (!DoubleBanking)
        {
            // Single banking: un solo pacco alla volta nella zona dello stopper; con intestatura di coda va al fermo scomparente
            if (_finalAssigned != null || Stopper.Package != null || _stops.Any(s => s.Package != null)) return -1;
            return _stops.Count > 0 ? 1 : 0;
        }
        if (_finalAssigned == null && Stopper.Package == null) return 0;
        for (int k = 0; k < _stops.Count; k++)
            if (_stops[k].Package == null) return k + 1;
        return -1;
    }

    private int NextFreeStopFor(PackageRuntime p)
    {
        int idx = FirstFreeStop();
        if (idx <= 0) return idx;
        if (!DoubleBanking)
        {
            // Primo fermo (da valle) dove il pacco ci sta di coda; altrimenti di testa sullo fermo finale
            for (int k = 0; k < _stops.Count; k++)
                if (FitsAtStop(p, k)) return k + 1;
            return 0;
        }
        // Double banking: secondo pacco solo se lui e quello davanti hanno il double banking (tabella Pacchi) e ci sta;
        // altrimenti aspetta e andrà da solo sul fermo finale
        var first = Stopper.Package ?? _finalAssigned;
        if (!PairsInDoubleBanking(p) || (first != null && !PairsInDoubleBanking(first))) return -1;
        if (!FitsAtStop(p, idx - 1)) return -1;
        // i due pacchi del gruppo non possono finire sullo stesso bilico (ognuno ha la sua pesatura)
        if (first != null && _scales.Count > 0)
        {
            double head = StopTargetHead(_stops[idx - 1], p);
            var mine = ScalesUnder(head - p.LengthM, head);
            var theirs = ScalesUnder(Stopper.PositionM - first.LengthM, Stopper.PositionM);
            if (mine.Overlaps(theirs)) return -1;
        }
        return idx;
    }

    private HashSet<ScaleRuntime> ScalesUnder(double tailM, double headM) =>
        _scales.Where(s => Math.Min(s.EndM, headM) - Math.Max(s.StartM, tailM) > 0.05).ToHashSet();

    private bool PairsInDoubleBanking(PackageRuntime p) => BankingOptions.Banking(Project, p.Type) == AccumulationMode.DoubleBanking;

    private double StopTargetHead(StopRuntime s, PackageRuntime p)
    {
        if (s.IsVirtual && s.TailPositioning)
            return Transfer.StartM + p.LengthM;     // coda a inizio trasferitore
        if (s.IsVirtual)
        {
            // Nessun fermo: la testa si ferma alla distanza impostata dalla coda del primo pacco
            var a = Stopper.Package ?? _finalAssigned;
            return a == null ? s.PositionM : Stopper.PositionM - a.LengthM - Project.Transfer.DoubleBankingGapMm / 1000.0;
        }
        return s.Reference == StopReference.Head ? s.PositionM : s.PositionM + p.LengthM;
    }

    /// <summary>
    /// Verifica geometrica: il pacco al fermo k deve stare sul trasferitore e nella zona, senza sovrapporsi
    /// al pacco del fermo a valle (distanza minima di sicurezza inclusa).
    /// </summary>
    private bool FitsAtStop(PackageRuntime p, int k)
    {
        var s = _stops[k];
        var zi = Topology.StopperZone!;
        double head = StopTargetHead(s, p);
        double tail = head - p.LengthM;
        double reach = head + s.OvershootM;   // con intestatura di coda il pacco supera il fermo prima di tornare indietro
        if (s.IsVirtual && s.TailPositioning)
            return head <= Stopper.PositionM + 1e-6;
        if (s.IsVirtual)
        {
            // Senza fermo: serve il primo pacco già destinato allo stopper; il secondo può sbalzare un poco oltre il trasferitore
            if ((Stopper.Package ?? _finalAssigned) == null) return false;
            return tail >= Math.Max(zi.StartM, Transfer.StartM - Project.Transfer.EffectiveMaxOverhangMm / 1000.0) - 1e-6;
        }
        if (tail < Math.Max(zi.StartM, Transfer.StartM) - 1e-6) return false;
        if (reach > Stopper.PositionM + 1e-6) return false;
        if (!DoubleBanking) return true;
        double aheadTail;
        if (k == 0)
        {
            var a = Stopper.Package ?? _finalAssigned;
            if (a == null) return false;
            aheadTail = Stopper.PositionM - a.LengthM;
        }
        else
        {
            var prev = _stops[k - 1];
            if (prev.Package == null) return false;
            aheadTail = prev.TargetHeadM - prev.Package.LengthM;
        }
        // Distanza di sicurezza sulla posizione finale; durante l'extra corsa dell'intestatura di coda basta un margine minimo
        return head <= aheadTail - _minSafety + 1e-6 && reach <= aheadTail - Math.Min(_minSafety, TailOvershootClearanceM) + 1e-6;
    }

    private void AssignStop(PackageRuntime p, int idx, ZoneRuntime z)
    {
        p.AssignedStop = idx;
        z.Assigned.Add(p);
        z.Reservations++;
        if (idx == 0)
        {
            if (!DoubleBanking && EffectiveHeading == StopReference.Tail && _reportedOnce.Add("tailfallback:" + p.Type.Id))
                Emit(SimEventType.StopPhase, EventSeverity.Warning,
                    $"{p.Id} ({p.Type.Id}, {p.LengthM * 1000:0} mm): intestatura di coda non possibile, il pacco non ci sta oltre il fermo scomparente; intestatura di testa sullo fermo finale", Project.Stopper.Id, p.Id);
            _finalAssigned = p;
            Emit(SimEventType.StopPhase, EventSeverity.Info, $"{p.Id} destinato allo fermo finale (testa a {Stopper.PositionM * 1000:0} mm)", Project.Stopper.Id, p.Id);
            return;
        }
        var s = _stops[idx - 1];
        s.Package = p;
        s.State = StopState.Assigned;
        s.TargetHeadM = StopTargetHead(s, p);
        s.Packages++;
        if (s.IsVirtual && s.TailPositioning)
        {
            s.PositionM = s.TargetHeadM;
            Emit(SimEventType.StopPhase, EventSeverity.Info,
                $"{p.Id} intestatura di coda senza fermo: fermato per posizionamento (encoder) con la coda a {(s.TargetHeadM - p.LengthM) * 1000:0} mm (inizio trasferitore)", null, p.Id);
            return;
        }
        if (s.IsVirtual)
        {
            s.PositionM = s.TargetHeadM;
            Emit(SimEventType.StopPhase, EventSeverity.Info,
                $"{p.Id} secondo pacco in double banking: fermato per posizionamento (encoder) con la testa a {s.TargetHeadM * 1000:0} mm, {Project.Transfer.DoubleBankingGapMm:0} mm dietro al primo", null, p.Id);
            return;
        }
        Emit(SimEventType.StopPhase, EventSeverity.Info,
            $"{p.Id} destinato al fermo {s.Id}, intestatura di {(s.Reference == StopReference.Head ? "testa" : "coda")} (testa a {s.TargetHeadM * 1000:0} mm, coda a {(s.TargetHeadM - p.LengthM) * 1000:0} mm)", s.Id, p.Id);
    }

    /// <summary>Comando della zona dello stopper con fermi scomparenti.</summary>
    private InverterMode MultiStopCommand(ZoneRuntime z, double t, double dt, out string reason, out bool autoStop)
    {
        autoStop = false;
        if (Stopper.State == StopperState.Aligning) { reason = "Allineamento allo fermo finale"; return InverterMode.Align; }
        var headAligning = _stops.FirstOrDefault(s => s.State == StopState.Aligning && s.Reference == StopReference.Head);
        if (headAligning != null) { reason = $"Intestatura di testa al fermo {headAligning.Id}"; return InverterMode.Align; }
        if (_finalAssigned != null && Stopper.Package == null) { reason = "Avanzamento verso lo fermo finale"; return InverterMode.Run; }
        var back = _stops.FirstOrDefault(s => s.State == StopState.Returning);
        if (back != null) { reason = $"Ritorno indietro: intestatura di coda al fermo {back.Id}"; return InverterMode.Reverse; }
        var active = _stops.FirstOrDefault(s => s.State is StopState.Assigned or StopState.Positioning);
        if (active != null && active.Package != null)
        {
            if (active.State == StopState.Assigned)
            {
                active.State = StopState.Positioning;
                Emit(SimEventType.StopPhase, EventSeverity.Info, $"{active.Id}: posizionamento di {active.Package.Id}", active.Id, active.Package.Id);
            }
            z.PositionCommand = PositioningProfile.Command(active.TargetHeadM + active.OvershootM - active.Package.HeadM, z.Config.HighSpeed, z.Config.Deceleration, dt);
            reason = active.Reference == StopReference.Tail
                ? $"Posizionamento oltre il fermo {active.Id} (intestatura di coda)"
                : $"Posizionamento al fermo {active.Id}";
            return InverterMode.Position;
        }
        if (_stops.Any(s => s.State == StopState.Aligning)) { reason = "Intestatura di coda in corso"; return InverterMode.Stop; }
        if (z.MultiCanAcceptNew && !ShouldAutoStop(z)) { reason = "Marcia: ingresso del pacco successivo"; return InverterMode.Run; }
        bool anyHeld = Stopper.Package != null || _stops.Any(s => s.Package != null);
        autoStop = z.MultiCanAcceptNew;
        reason = anyHeld ? "Pacchi allineati: attesa trasferitore/pacco successivo" : "Arresto automatico: nessun pacco da movimentare";
        return InverterMode.Stop;
    }

    private void UpdateIntermediateStops(double dt)
    {
        var zone = Topology.StopperZone != null ? _zoneById[Topology.StopperZone.Id] : null;
        foreach (var s in _stops)
        {
            var p = s.Package;
            if (p != null) s.OccupiedTimeS += dt;
            bool clear = !_active.Any(q => q.OnRollers && q != p && q.TailM < s.PositionM - 1e-6 && q.HeadM > s.PositionM + 1e-6);
            s.Raised = !s.IsVirtual && p != null && clear && (s.Reference == StopReference.Head
                       || (s.State is StopState.Returning or StopState.Aligning or StopState.Aligned && p.TailM >= s.PositionM - s.ToleranceM));
            if (p == null) continue;
            if (s.State == StopState.Returning)
            {
                // Il fermo è sollevato dietro al pacco: il pacco torna indietro fino a battere di coda
                double newHead = Math.Max(s.TargetHeadM, p.HeadM - s.ReturnSpeed * dt);
                p.HeadM = newHead;
                p.Velocity = 0;
                _lastProgressTime = Time + dt;
                if (newHead <= s.TargetHeadM + 1e-9)
                {
                    p.HeadM = s.TargetHeadM;
                    s.State = StopState.Aligning;
                    s.ElapsedS = 0;
                    p.State = PackageState.Aligning;
                    Emit(SimEventType.StopPhase, EventSeverity.Info,
                        $"{p.Id} in battuta di coda al fermo {s.Id} (testa {p.HeadM * 1000:0} mm, coda {p.TailM * 1000:0} mm)", s.Id, p.Id);
                }
                continue;
            }
            if (s.State == StopState.Positioning)
            {
                double r = s.TargetHeadM + s.OvershootM - p.HeadM;
                bool arrived = r <= 2e-4 && r >= -s.ToleranceM;
                bool overshoot = r < -s.ToleranceM;
                if (!(arrived || (overshoot && p.IsStopped))) continue;
                if (overshoot)
                {
                    PositioningErrors++;
                    Emit(SimEventType.PositioningError, EventSeverity.Error, $"{p.Id} oltre il fermo {s.Id} di {-r * 1000:0.0} mm", s.Id, p.Id);
                }
                else p.HeadM = s.TargetHeadM + s.OvershootM;
                p.Velocity = 0;
                if (zone != null) { zone.ActualSpeed = 0; zone.PositionCommand = 0; }
                if (!clear && !s.IsVirtual)
                {
                    InterlockEvents++;
                    Alarm(SimEventType.Interlock, $"{s.Id}: fermo non sollevabile, un pacco occupa la posizione del fermo", s.Id, p.Id);
                }
                if (s.Reference == StopReference.Tail)
                {
                    p.HeldByTailStop = true;
                    s.State = StopState.Returning;
                    Emit(SimEventType.StopPhase, EventSeverity.Info,
                        $"{p.Id} oltre il fermo {s.Id} di {s.OvershootM * 1000:0} mm: fermo sollevato, ritorno indietro a {s.ReturnSpeed:0.##} m/s", s.Id, p.Id);
                    continue;
                }
                s.State = StopState.Aligning;
                s.ElapsedS = 0;
                p.State = PackageState.Aligning;
                Emit(SimEventType.StopPhase, EventSeverity.Info,
                    $"{p.Id} al fermo {s.Id}: intestatura di {(s.Reference == StopReference.Head ? "testa" : "coda")} (testa {p.HeadM * 1000:0} mm, coda {p.TailM * 1000:0} mm)", s.Id, p.Id);
            }
            else if (s.State == StopState.Aligning)
            {
                s.ElapsedS += dt;
                if (s.ElapsedS >= s.Config.AlignmentTimeS - 1e-9)
                {
                    s.State = StopState.Aligned;
                    p.State = PackageState.Aligned;
                    Emit(SimEventType.AlignmentCompleted, EventSeverity.Info, $"{p.Id} allineato al fermo {s.Id} ({s.Config.AlignmentTimeS:0.##} s)", s.Id, p.Id);
                }
            }
        }
    }

    /// <summary>Gruppo di pacchi pronto per il trasferitore (null se non ancora pronto).</summary>
    private List<PackageRuntime>? ReadyBatch(double t)
    {
        // Mai sollevare mentre un altro pacco sta entrando nell'area del trasferitore: prima si ferma (e, se ci sta, entra nel gruppo)
        // né mentre un pacco in arrivo è così vicino da non potersi più fermare prima dell'area (spazio di frenata + 0,5 m)
        if (_active.Any(x => x.OnRollers && Math.Abs(x.Velocity) > 1e-3 && x.TailM < Transfer.EndM - 1e-6
                             && x.HeadM > Transfer.StartM - (x.Velocity * x.Velocity / (2 * 0.8) + 0.5)))
            return null;
        if (_stops.Count == 0)
            return Stopper.State == StopperState.Aligned && Stopper.Package != null ? new List<PackageRuntime> { Stopper.Package } : null;

        var aligned = new List<PackageRuntime>();
        if (Stopper.State == StopperState.Aligned && Stopper.Package != null) aligned.Add(Stopper.Package);
        foreach (var s in _stops) if (s.State == StopState.Aligned && s.Package != null) aligned.Add(s.Package);
        if (aligned.Count == 0) { _batchWaitSince = null; return null; }
        bool approaching = (_finalAssigned != null && Stopper.State != StopperState.Aligned)
                           || _stops.Any(s => s.State is StopState.Assigned or StopState.Positioning or StopState.Returning or StopState.Aligning);
        if (approaching) return null;
        _batchWaitSince ??= t;
        int target = DoubleBanking ? Math.Min(2, 1 + _stops.Count) : 1;
        if (aligned.Count >= target) return aligned;
        var zone = _zoneById[Topology.StopperZone!.Id];
        var cand = NextCandidate(zone);
        bool more = cand != null ? NextFreeStopFor(cand) >= 0 : _nextIndex < Project.Infeed.PackageCount && FirstFreeStop() >= 0;
        if (!more) return aligned;
        if (t - _batchWaitSince.Value >= Transfer.Config.BatchWaitTimeoutS - 1e-9)
        {
            Emit(SimEventType.TransferWaiting, EventSeverity.Info,
                $"Trasferitore: attesa gruppo scaduta ({Transfer.Config.BatchWaitTimeoutS:0} s), prelievo di {aligned.Count} pacchi", Project.Transfer.Id);
            return aligned;
        }
        return null;
    }

    private void ReleaseStopsFor(List<PackageRuntime> lifted)
    {
        if (_finalAssigned != null && lifted.Contains(_finalAssigned)) _finalAssigned = null;
        foreach (var s in _stops)
        {
            if (s.Package == null || !lifted.Contains(s.Package)) continue;
            s.Package = null;
            s.State = StopState.Free;
            s.Raised = false;
            s.ElapsedS = 0;
        }
        if (Topology.StopperZone != null) _zoneById[Topology.StopperZone.Id].Assigned.RemoveAll(lifted.Contains);
    }

    // ================================================================== Bilici di pesatura

    /// <summary>Pianifica i bilici di destinazione: false se un bilico necessario è ancora occupato (interblocco).</summary>
    private bool TryPlanScales(List<PackageRuntime> pkgs) => TryPlanScales(pkgs, Array.Empty<PackageRuntime>());

    /// <summary>
    /// Bilici di destinazione dei pacchi. <paramref name="leaving"/>: pacchi pesati che lasciano il bilico nello stesso ciclo
    /// (trasferitore a due prese): i loro bilici contano come liberi.
    /// </summary>
    private bool TryPlanScales(List<PackageRuntime> pkgs, IReadOnlyCollection<PackageRuntime> leaving)
    {
        _scalePlan.Clear();
        var used = new HashSet<ScaleRuntime>();
        foreach (var p in pkgs)
        {
            var cover = _scales.Where(s => Math.Min(s.EndM, p.HeadM) - Math.Max(s.StartM, p.TailM) > 0.05).OrderBy(s => s.StartM).ToList();
            if (cover.Count == 0)
            {
                if (_reportedOnce.Add("noscale:" + p.Id))
                    Emit(SimEventType.DeviceAlarm, EventSeverity.Warning, $"{p.Id}: nessun bilico sotto il pacco, deposito senza pesatura", Project.Transfer.Id, p.Id);
                _scalePlan[p] = new List<ScaleRuntime>();
                continue;
            }
            if (cover.Any(used.Contains))
            {
                if (_reportedOnce.Add("scaleshare:" + p.Id))
                    Alarm(SimEventType.DeviceAlarm, $"{p.Id}: bilico già destinato a un altro pacco dello stesso gruppo, deposito senza pesatura", Project.Transfer.Id, p.Id);
                _scalePlan[p] = new List<ScaleRuntime>();
                continue;
            }
            foreach (var s in cover) used.Add(s);
            _scalePlan[p] = cover;
        }
        if (used.Any(s => s.Busy && !(s.Package != null && leaving.Contains(s.Package)))) { _scalePlan.Clear(); return false; }
        return true;
    }

    private void StartWeighing(PackageRuntime p, List<ScaleRuntime> scales, double t)
    {
        var lead = scales[0];
        lead.Lead = null;
        lead.Followers.Clear();
        lead.Package = p;
        foreach (var f in scales.Skip(1))
        {
            f.Package = p;
            f.Lead = lead;
            f.State = ScaleState.Directed;
            lead.Followers.Add(f);
        }
        lead.State = ScaleState.Directed;
        lead.ElapsedS = 0;
        lead.Speed = 0;
        lead.Cycles++;
        lead.FaultPending = lead.Config.SimulatedFaultAtCycle > 0 && lead.Cycles == lead.Config.SimulatedFaultAtCycle;
        p.State = PackageState.OnScale;
        p.Velocity = 0;
        p.ScaleId = string.Join("+", scales.Select(s => s.Id));
        var cfg = lead.Config;
        string where;
        if (lead.Combined)
        {
            lead.TargetHeadM = p.HeadM;
            lead.ShortPosition = false;
            where = "pesatura combinata, nessuno spostamento";
        }
        else
        {
            // Sul bilico il pacco non si muove: viene pesato dove il trasferitore lo deposita
            lead.ShortPosition = false;
            lead.TargetHeadM = p.HeadM;
            where = "pesatura sul punto di deposito, il pacco non si muove sul bilico";
        }
        Emit(SimEventType.WeighingPhase, EventSeverity.Info,
            $"{p.Id} indirizzato al bilico {p.ScaleId} ({where}, testa a {lead.TargetHeadM * 1000:0} mm)", lead.Id, p.Id);
    }

    private void UpdateScales(double t, double dt)
    {
        foreach (var lead in _scales.Where(s => s.Package != null && s.Lead == null).ToList())
        {
            var p = lead.Package!;
            var cfg = lead.Config;
            lead.BusyTimeS += dt;
            foreach (var f in lead.Followers) f.BusyTimeS += dt;
            bool hold = StopActive(cfg);
            switch (lead.State)
            {
                case ScaleState.Directed:
                    lead.State = Math.Abs(lead.TargetHeadM - p.HeadM) <= 2e-4 ? ScaleState.PositionCheck : ScaleState.Positioning;
                    if (lead.State == ScaleState.Positioning)
                        Emit(SimEventType.WeighingPhase, EventSeverity.Info, $"{lead.Id}: arresto e posizionamento di {p.Id}", lead.Id, p.Id);
                    break;
                case ScaleState.Positioning:
                {
                    if (hold) break;
                    double r = lead.TargetHeadM - p.HeadM;
                    double dist = Math.Abs(r), dir = Math.Sign(r);
                    double vcmd = PositioningProfile.Command(dist, cfg.PositioningSpeed, cfg.Acceleration, dt);
                    double vOld = lead.Speed;
                    double vNew = vOld < vcmd ? Math.Min(vcmd, vOld + cfg.Acceleration * dt) : vcmd;
                    double step = 0.5 * (vOld + vNew) * dt;
                    if (dist <= 2e-4 || step >= dist)
                    {
                        p.HeadM = lead.TargetHeadM;
                        lead.Speed = 0;
                        p.Velocity = 0;
                        lead.State = ScaleState.PositionCheck;
                    }
                    else
                    {
                        p.HeadM += dir * step;
                        lead.Speed = vNew;
                        p.Velocity = dir * vNew;
                        _lastProgressTime = t;
                    }
                    break;
                }
                case ScaleState.PositionCheck:
                {
                    double sStart = lead.Combined ? Math.Min(lead.StartM, lead.Followers.Min(f => f.StartM)) : lead.StartM;
                    double sEnd = lead.Combined ? Math.Max(lead.EndM, lead.Followers.Max(f => f.EndM)) : lead.EndM;
                    double tol = lead.ToleranceM;
                    bool ok = p.TailM >= sStart - tol && p.HeadM <= sEnd + tol && Math.Abs(p.HeadM - lead.TargetHeadM) <= tol;
                    if (ok)
                        Emit(SimEventType.WeighingPhase, EventSeverity.Info, $"{lead.Id}: posizionamento di {p.Id} verificato, stabilizzazione", lead.Id, p.Id);
                    else
                    {
                        p.WeighingValid = false;
                        Alarm(SimEventType.DeviceAlarm, $"{lead.Id}: {p.Id} non correttamente posizionato sul bilico (pacco {p.TailM * 1000:0}-{p.HeadM * 1000:0} mm), pesata non valida", lead.Id, p.Id);
                    }
                    lead.State = ScaleState.Stabilizing;
                    lead.ElapsedS = 0;
                    break;
                }
                case ScaleState.Stabilizing:
                    if (hold) break;
                    lead.ElapsedS += dt;
                    if (lead.FaultPending && lead.ElapsedS >= cfg.ConfirmTimeoutS - 1e-9)
                    {
                        lead.State = ScaleState.Fault;
                        lead.ElapsedS = 0;
                        lead.Faults++;
                        Alarm(SimEventType.DeviceAlarm, $"{lead.Id}: peso non stabile entro {cfg.ConfirmTimeoutS:0.#} s (guasto)", lead.Id, p.Id);
                    }
                    else if (!lead.FaultPending && lead.ElapsedS >= cfg.StabilizationTimeS - 1e-9)
                    {
                        lead.State = ScaleState.Acquiring;
                        lead.ElapsedS = 0;
                    }
                    break;
                case ScaleState.Fault:
                    lead.ElapsedS += dt;
                    if (lead.ElapsedS >= cfg.FaultResetTimeS - 1e-9)
                    {
                        lead.FaultPending = false;
                        lead.State = ScaleState.Stabilizing;
                        lead.ElapsedS = 0;
                        Emit(SimEventType.WeighingPhase, EventSeverity.Info, $"{lead.Id}: guasto ripristinato, nuova stabilizzazione", lead.Id, p.Id);
                    }
                    break;
                case ScaleState.Acquiring:
                    lead.ElapsedS += dt;
                    if (lead.ElapsedS >= cfg.AcquisitionTimeS - 1e-9)
                    {
                        double res = cfg.ResolutionKg > 0 ? cfg.ResolutionKg : 1;
                        double kg = Math.Round(p.WeightKg / res, MidpointRounding.AwayFromZero) * res;
                        p.MeasuredWeightKg = kg;
                        p.WeighedTimeS = t;
                        lead.LastMeasuredKg = kg;
                        lead.Weighings++;
                        WeighedPackages++;
                        Emit(SimEventType.WeightAcquired, EventSeverity.Info, $"{p.ScaleId}: peso di {p.Id} acquisito = {kg:0} kg{(p.WeighingValid ? "" : " (NON VALIDO)")}", lead.Id, p.Id);
                        double capacity = cfg.CapacityKg + lead.Followers.Sum(f => f.Config.CapacityKg);
                        if (kg > capacity)
                            Alarm(SimEventType.DeviceAlarm, $"{lead.Id}: peso {kg:0} kg oltre la portata {capacity:0} kg", lead.Id, p.Id);
                        lead.State = ScaleState.Completed;
                        lead.ElapsedS = 0;
                        Emit(SimEventType.WeighingPhase, EventSeverity.Info, $"{lead.Id}: pesatura di {p.Id} completata", lead.Id, p.Id);
                    }
                    break;
                case ScaleState.Completed:
                    if (Transfer.TwoPick) break;                // a due prese il pacco pesato lo porta via il trasferitore
                    if (!hold && Consent(cfg))
                    {
                        lead.State = ScaleState.Releasing;
                        lead.ElapsedS = 0;
                        Emit(SimEventType.WeighingPhase, EventSeverity.Info, $"{lead.Id}: consenso a proseguire, scarico di {p.Id}", lead.Id, p.Id);
                    }
                    break;
                case ScaleState.Releasing:
                    lead.ElapsedS += dt;
                    if (lead.ElapsedS >= cfg.ReleaseTimeS - 1e-9)
                    {
                        p.State = PackageState.Exited;
                        p.ExitTimeS = t;
                        _active.Remove(p);
                        Emit(SimEventType.PackageExited, EventSeverity.Info,
                            $"{p.Id} pesato ({p.MeasuredWeightKg:0} kg) e scaricato dal bilico {p.ScaleId} (attraversamento {p.LeadTimeS:0.0} s)", lead.Id, p.Id);
                        foreach (var f in lead.Followers) { f.State = ScaleState.Idle; f.Package = null; f.Lead = null; }
                        lead.Followers.Clear();
                        lead.State = ScaleState.Idle;
                        lead.Package = null;
                        lead.ElapsedS = 0;
                        continue;
                    }
                    break;
            }
            foreach (var f in lead.Followers) f.State = lead.State;
        }

        // Verifica sovrapposizioni tra pacchi sui bilici
        var onScale = _active.Where(x => x.State == PackageState.OnScale).OrderBy(x => x.HeadM).ToList();
        for (int i = 1; i < onScale.Count; i++)
        {
            var a = onScale[i - 1]; var b = onScale[i];
            if (b.TailM < a.HeadM - 1e-6 && _reportedOnce.Add($"scaleoverlap:{a.Id}:{b.Id}"))
            {
                Collisions++;
                Emit(SimEventType.Collision, EventSeverity.Error, $"COLLISIONE sui bilici: {a.Id} e {b.Id} sovrapposti", null, b.Id);
            }
        }
    }

    // ================================================================== Stato dispositivi (logica DEV, UI)

    /// <summary>Stato testuale e impegno di un dispositivo per identificativo (null se inesistente).</summary>
    public (string State, bool Busy)? DeviceStatus(string id)
    {
        var s = _strappers.FirstOrDefault(x => x.Id == id);
        if (s != null) return (s.ProcessState.ToString(), s.ProcessState != StrapperProcessState.Available);
        if (id == "STATION" && Station != null) return (Station.State.ToString(), Station.State != StationState.Idle);
        if (BundleClamp != null && BundleClamp.Id == id) return (BundleClamp.State.ToString(), !BundleClamp.IsOpen);
        if (Compactor != null && Compactor.Id == id) return (Compactor.State.ToString(), !Compactor.IsOpen);
        if (Rejector != null && Rejector.Id == id) return (Rejector.State.ToString(), Rejector.Busy);
        var stop = _stops.FirstOrDefault(x => x.Id == id);
        if (stop != null) return (stop.State.ToString(), stop.State != StopState.Free);
        var sc = _scales.FirstOrDefault(x => x.Id == id);
        if (sc != null) return (sc.State.ToString(), sc.Busy);
        if (Project.Transfer.Id == id) return (Transfer.State.ToString(), Transfer.State != TransferState.Home);
        if (Project.Stopper.Id == id) return (Stopper.State.ToString(), Stopper.Package != null);
        return null;
    }
}
