using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Engine;

/// <summary>
/// Trasferitore sollevabile a due prese con il bilico a metà corsa e trasferitore d'accumulo in uscita
/// (richiesta dell'utente del 2026-11-08):
/// <list type="bullet">
/// <item>a ogni ciclo il trasferitore solleva, avanza di mezza corsa, abbassa e deposita, poi torna indietro di mezza corsa;</item>
/// <item>il pacco nuovo va dalla via rulli al bilico; quello già pesato va dal bilico al trasferitore d'accumulo, nello stesso ciclo;</item>
/// <item>al primo ciclo c'è solo il pacco della via rulli; il pacco pesato parte da solo solo quando sulla via rulli non arrivano più pacchi;</item>
/// <item>il trasferitore d'accumulo, a ogni deposito, avanza di larghezza pacco + 50 mm; i pacchi in fondo vengono scaricati.</item>
/// </list>
/// Per i risultati il pacco esce dalla linea quando arriva sul trasferitore d'accumulo.
/// </summary>
public sealed partial class SimulationEngine
{
    public AccumulationRuntime? Accumulation { get; private set; }
    private double? _scaleReadySince;

    private void InitTwoPick()
    {
        var p = Project;
        Transfer.TwoPick = p.Transfer.ScaleAtMidStroke && p.Transfer.Kind == TransferKind.Lifting && _scales.Count > 0;
        if (Transfer.TwoPick && p.AccumulationTransfer.IsEnabled)
            Accumulation = new AccumulationRuntime(p.AccumulationTransfer);
        if (Transfer.TwoPick)
            Emit(SimEventType.TransferPhase, EventSeverity.Info,
                $"Trasferitore a due prese: bilico a metà corsa ({p.Transfer.TranslationStrokeMm / 2:0} mm), " +
                (Accumulation != null ? $"uscita sul trasferitore d'accumulo ({p.AccumulationTransfer.WidthMm:0} mm, passo = larghezza pacco + {p.AccumulationTransfer.StepGapMm:0} mm)" : "uscita alla stazione successiva"),
                p.Transfer.Id);
    }

    /// <summary>Pacchi sul bilico già pesati, pronti per la presa (capofila in stato Completato).</summary>
    private List<PackageRuntime> WeighedOnScale() =>
        _scales.Where(s => s.Lead == null && s.Package != null && s.State == ScaleState.Completed)
            .SelectMany(s => s.Companion != null ? new[] { s.Companion, s.Package! } : new[] { s.Package! }).Distinct().ToList();

    /// <summary>Pacco a monte (pesato per primo) e a valle del gruppo sullo stesso bilico.</summary>
    private (PackageRuntime Monte, PackageRuntime Valle) SharedPair(TransferRuntime tr)
    {
        var o = tr.Packages.OrderBy(p => p.HeadM).ToList();
        return (o[0], o[^1]);
    }

    private bool ScaleWorking() => _scales.Any(s => s.Package != null && s.State != ScaleState.Completed);

    private void UpdateTwoPickTransfer(double t, double dt)
    {
        var tr = Transfer;
        var cfg = tr.Config;
        double half = cfg.TranslationStrokeMm / 1000.0 / 2, lift = cfg.LiftStrokeMm / 1000.0;

        if (tr.State == TransferState.Home)
        {
            tr.LiftM = 0; tr.LateralM = 0;
            var weighed = WeighedOnScale();
            if (weighed.Count > 0) _scaleReadySince ??= t; else _scaleReadySince = null;
            bool exitFree = Accumulation == null || !Accumulation.Stepping;
            if (ScaleWorking() || (weighed.Count > 0 && !exitFree)) return;     // bilico in pesatura o uscita occupata: si aspetta

            var batch = ReadyBatch(t);
            if (batch != null && !TryPlanScales(batch, weighed)) batch = null;
            // Il pacco pesato parte da solo solo se sulla via rulli non c'è più nessun pacco (fine produzione):
            // altrimenti aspetta il pacco successivo e partono insieme (mai con un pacco in arrivo nell'area del trasferitore)
            bool aloneFromScale = batch == null && weighed.Count > 0 && !_active.Any(p => p.OnRollers);
            if (batch == null && !aloneFromScale) return;

            tr.Packages.Clear();
            tr.FromScale.Clear();
            if (batch != null) tr.Packages.AddRange(batch);
            foreach (var pk in weighed)
            {
                TakeFromScale(pk);
                tr.FromScale.Add(pk);
            }
            _batchWaitSince = null;
            _scaleReadySince = null;
            SetTransferPhase(tr, TransferState.Lifting, t);
            return;
        }

        tr.BusyTimeS += dt;
        if (tr.State == TransferState.WeighingUpstream)
        {
            // Doppio comando: la metà a valle resta sollevata finché il pacco a monte non è pesato
            var (monte, valle) = SharedPair(tr);
            valle.LiftM = lift; valle.LateralM = half;
            foreach (var pk in tr.FromScale) { pk.LiftM = lift; pk.LateralM = 2 * half; }
            var lead = _scales.FirstOrDefault(x => x.Package == monte && x.Lead == null);
            if (lead == null || lead.State != ScaleState.Completed) return;
            SetTransferPhase(tr, TransferState.LoweringDownstream, t);
            Link("PESA_DIFF", "Pacco a monte pesato", "Abbassa la metà a valle: pesata del totale, secondo per differenza", "SCALE:" + lead.Id, "TRANSFER", valle.Id);
            return;
        }
        tr.ElapsedS += dt;
        double dur = tr.PhaseDuration(tr.State);
        double f = dur > 0 ? Math.Clamp(tr.ElapsedS / dur, 0, 1) : 1;
        switch (tr.State)
        {
            case TransferState.Lifting: tr.LiftM = lift * f; break;
            case TransferState.Translating: tr.LiftM = lift; tr.LateralM = half * f; break;
            case TransferState.Lowering: tr.LiftM = lift * (1 - f); tr.LateralM = half; break;
            case TransferState.LoweringDownstream: tr.LiftM = lift * (1 - f); tr.LateralM = half; break;
            case TransferState.Depositing: tr.LiftM = 0; tr.LateralM = half; break;
            case TransferState.Returning: tr.LiftM = 0; tr.LateralM = half * (1 - f); break;
        }
        if (tr.State != TransferState.Returning)
        {
            double pl = tr.State == TransferState.Depositing ? 0 : tr.LiftM;
            foreach (var pk in tr.Packages) { pk.LiftM = pl; pk.LateralM = tr.LateralM; }
            foreach (var pk in tr.FromScale) { pk.LiftM = pl; pk.LateralM = half + tr.LateralM; }
            if (_sharedPair && tr.Packages.Count == 2)
            {
                // Doppio comando: scende prima la metà a monte (il pacco a valle e quelli presi dal bilico restano su)
                var (monte, valle) = SharedPair(tr);
                if (tr.State == TransferState.Lowering) { valle.LiftM = lift; foreach (var pk in tr.FromScale) pk.LiftM = lift; }
                if (tr.State == TransferState.LoweringDownstream) monte.LiftM = 0;
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
                SetTransferPhase(tr, TransferState.Lowering, t);
                break;
            case TransferState.Lowering:
                if (_sharedPair && tr.Packages.Count == 2)
                {
                    var (monte, _) = SharedPair(tr);
                    monte.LiftM = 0;
                    monte.LateralM = half;
                    StartWeighing(monte, _scalePlan[monte], t);
                    SetTransferPhase(tr, TransferState.WeighingUpstream, t);
                    break;
                }
                SetTransferPhase(tr, TransferState.Depositing, t);
                break;
            case TransferState.LoweringDownstream:
            {
                var (monte, valle) = SharedPair(tr);
                valle.LiftM = 0;
                valle.LateralM = half;
                StartWeighing(valle, _scalePlan[valle], t, companion: monte);
                SetTransferPhase(tr, TransferState.Depositing, t);
                break;
            }
            case TransferState.Depositing:
                foreach (var pk in tr.Packages)
                {
                    if (_sharedPair) break;             // già depositati uno alla volta dal doppio comando
                    pk.LiftM = 0;
                    pk.LateralM = half;
                    if (_scalePlan.TryGetValue(pk, out var scales) && scales.Count > 0) StartWeighing(pk, scales, t);
                    else ExitToNextStation(pk, t, "senza pesatura");
                }
                foreach (var pk in tr.FromScale)
                {
                    pk.LiftM = 0;
                    pk.LateralM = 2 * half;
                    if (Accumulation != null) DepositOnAccumulation(pk, t);
                    else ExitToNextStation(pk, t, $"pesato ({pk.MeasuredWeightKg:0} kg)");
                }
                _scalePlan.Clear();
                _sharedPair = false;
                tr.Packages.Clear();
                tr.FromScale.Clear();
                tr.Cycles++;
                SetTransferPhase(tr, TransferState.Returning, t);
                break;
            case TransferState.Returning:
                tr.LateralM = 0;
                SetTransferPhase(tr, TransferState.Home, t);
                break;
        }
    }

    /// <summary>Il trasferitore prende il pacco pesato: il bilico si libera.</summary>
    private void TakeFromScale(PackageRuntime p)
    {
        foreach (var s in _scales.Where(s => s.Package == p || s.Companion == p))
        {
            s.State = ScaleState.Idle;
            s.Package = null;
            s.Companion = null;
            s.Lead = null;
            s.Followers.Clear();
            s.ElapsedS = 0;
        }
        p.State = PackageState.OnTransfer;
        Emit(SimEventType.WeighingPhase, EventSeverity.Info, $"{p.Id} preso dal bilico {p.ScaleId} dal trasferitore", Project.Transfer.Id, p.Id);
    }

    private void ExitToNextStation(PackageRuntime p, double t, string note)
    {
        p.State = PackageState.Exited;
        p.ExitTimeS = t;
        _active.Remove(p);
        Emit(SimEventType.PackageExited, EventSeverity.Info, $"{p.Id} {note}, depositato nella stazione successiva (attraversamento {p.LeadTimeS:0.0} s)", Project.Transfer.Id, p.Id);
    }

    /// <summary>Deposito sul trasferitore d'accumulo: il pacco esce dalla linea e il trasferitore avanza di un passo.</summary>
    private void DepositOnAccumulation(PackageRuntime p, double t)
    {
        var acc = Accumulation!;
        p.State = PackageState.Exited;
        p.ExitTimeS = t;
        _active.Remove(p);
        double w = p.Type.GraphicWidthMm / 1000.0;
        acc.Items.Add(new AccumulationItem { Package = p, OffsetM = 0, WidthM = w });
        acc.Received++;
        acc.StepRemainingM = w + acc.Config.StepGapMm / 1000.0;
        Emit(SimEventType.PackageExited, EventSeverity.Info,
            $"{p.Id} pesato ({p.MeasuredWeightKg:0} kg) e depositato sul trasferitore d'accumulo, che avanza di {acc.StepRemainingM * 1000:0} mm (attraversamento {p.LeadTimeS:0.0} s)",
            acc.Config.Id, p.Id);
    }

    private void UpdateAccumulation(double t, double dt)
    {
        var acc = Accumulation;
        if (acc == null || !acc.Stepping) return;
        acc.BusyTimeS += dt;
        double v = Math.Max(0.01, acc.Config.StepSpeed);
        double move = Math.Min(acc.StepRemainingM, v * dt);
        foreach (var it in acc.Items) it.OffsetM += move;
        acc.StepRemainingM -= move;
        if (acc.Stepping) return;
        acc.StepRemainingM = 0;
        // in fondo al trasferitore d'accumulo i pacchi vengono scaricati
        foreach (var it in acc.Items.Where(i => i.OffsetM + i.WidthM / 2 > acc.WidthM - i.WidthM / 2 + 1e-6).ToList())
        {
            acc.Items.Remove(it);
            acc.Unloaded++;
            Emit(SimEventType.TransferPhase, EventSeverity.Info, $"{it.Package.Id} arrivato in fondo al trasferitore d'accumulo: scaricato", acc.Config.Id, it.Package.Id);
        }
    }

    /// <summary>Istantanee dei pacchi sul trasferitore d'accumulo (per la vista 3D e il Gantt).</summary>
    private IEnumerable<PackageSnapshot> AccumulationSnapshots()
    {
        if (Accumulation == null) yield break;
        double entry = Project.Transfer.TranslationStrokeMm / 1000.0;
        foreach (var it in Accumulation.Items)
        {
            var p = it.Package;
            yield return new PackageSnapshot(p.Id, p.Type.Id, p.HeadM, p.LengthM, 0, PackageState.OnAccumulation,
                0, entry + it.OffsetM, p.StrapsDone, p.StrapsTotal, p.Type.GraphicWidthMm, p.Type.GraphicHeightMm);
        }
    }
}
