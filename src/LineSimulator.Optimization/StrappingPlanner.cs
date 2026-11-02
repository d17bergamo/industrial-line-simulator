using System.Globalization;
using System.Text;
using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Optimization;

/// <summary>Confronto tra strategia automatica, predefinita e manuale per una tipologia di pacco.</summary>
public sealed class StrategyComparison
{
    public required string PackageTypeId { get; init; }
    public required StrappingStrategyMode Mode { get; init; }
    public required OptimizationResult Automatic { get; init; }
    public required StrappingPlan Default { get; init; }
    public StrappingPlan? Manual { get; init; }
    /// <summary>Piano effettivamente usato dalla simulazione (secondo la modalità configurata).</summary>
    public required StrappingPlan Selected { get; init; }
    public required string SelectionReason { get; init; }
    public string? Error { get; init; }
}

/// <summary>Costruisce il problema di legatura dal progetto e produce il confronto tra strategie.</summary>
public static class StrappingPlanner
{
    public static StrappingProblem? BuildProblem(LineProject project, LineTopology topo, PackageType type, out string? error)
    {
        error = null;
        var zone = topo.StrappingZone;
        if (zone == null) { error = "zona di legatura non definita o non valida"; return null; }
        // Dispositivi di fermata: compattatore (prima della legatura) e stringifascio KNOF (chiuso durante la legatura).
        // Il tempo di fermata diventa: pre + max_i(prep_i + leg_i + max(rit_i, post)), equivalente a cicli "efficaci"
        // per legatrice: (prep_i + pre) + leg_i + max(rit_i, post). L'ottimizzatore esatto resta invariato.
        var (pre, post) = StopOverhead(project);
        // Un solo tipo di legatura per pacco: filo → KNCA, reggia d'acciaio → SMA
        var machine = type.BindingType == BindingType.SteelStrap ? StrapperMachineType.SMA : StrapperMachineType.KNCA;
        var specs = project.Strappers
            .Select((s, i) => (s, i))
            .Where(x => x.s.IsEnabled && x.s.MachineType == machine)
            .Select(x => new StrapperSpec(x.s.Id, x.s.Name, x.i, Units.MmToM(x.s.PositionMm),
                x.s.PreparationTimeS + pre, x.s.StrappingTimeS, Math.Max(x.s.ReturnTimeS, post), x.s.AllowCombinedWork))
            .ToList();
        if (specs.Count == 0)
        {
            error = machine == StrapperMachineType.SMA ? "nessuna reggettatrice SMA abilitata per un pacco con reggia" : "nessuna legatrice KNCA abilitata per un pacco legato a filo";
            return null;
        }
        double L = Units.MmToM(type.LengthMm);
        double headMin = zone.StartM + L, headMax = zone.EndM;
        // Il pacco deve trovarsi sotto stringifascio e compattatore a ogni fermata: H − L ≤ X ≤ H
        if (project.StrappingStation.IsEnabled && project.BundleClamp.IsEnabled)
        {
            double x = Units.MmToM(project.BundleClamp.PositionMm);
            headMin = Math.Max(headMin, x); headMax = Math.Min(headMax, x + L);
        }
        if (project.StrappingStation.IsEnabled && project.Compactor.IsEnabled)
        {
            double x = Units.MmToM(project.Compactor.PositionMm);
            headMin = Math.Max(headMin, x); headMax = Math.Min(headMax, x + L);
        }
        if (headMin > headMax + 1e-9) { error = "nessuna posizione di legatura con il pacco sotto stringifascio e compattatore"; return null; }
        return new StrappingProblem
        {
            Strappers = specs,
            StrapPositionsM = type.StrapPositionsMm.Select(Units.MmToM).ToList(),
            PackageLengthM = L,
            HeadMinM = headMin,
            HeadMaxM = headMax,
            ReferenceHeadM = zone.StartM,
            ExitHeadM = zone.EndM + L,
            TransportSpeed = zone.Inverter.HighSpeed,
            PositioningSpeed = project.StrappingStation.PositioningSpeed,
            Acceleration = zone.Inverter.Acceleration,
            Deceleration = zone.Inverter.Deceleration,
            MatchToleranceM = Math.Max(0.0005, Units.MmToM(project.StrappingStation.PositioningToleranceMm) / 2)
        };
    }

    /// <summary>
    /// Tempi aggiuntivi per fermata dovuti a compattatore e stringifascio:
    /// pre = chiusura + conferma (+ mantenimento compattatore) prima della legatura;
    /// post = mantenimento + apertura + conferma dopo la fine della legatura (sovrapposto al ritorno testa).
    /// </summary>
    public static (double Pre, double Post) StopOverhead(LineProject project)
    {
        if (!project.StrappingStation.IsEnabled) return (0, 0);
        var k = project.BundleClamp;
        var c = project.Compactor;
        double pre = 0, post = 0;
        if (c.IsEnabled) pre += c.CloseTimeS + c.ConfirmDelayS + c.HoldTimeS;
        if (k.IsEnabled) pre += k.CloseTimeS + k.ConfirmDelayS;
        double clampPost = k.IsEnabled ? k.HoldTimeS + k.OpenTimeS + k.ConfirmDelayS : 0;
        double compOpen = c.IsEnabled ? c.OpenTimeS + c.ConfirmDelayS : 0;
        if (!c.IsEnabled) post = clampPost;
        else if (c.OpenPhase == CompactorOpenPhase.AfterBundleClampOpened) post = clampPost + compOpen;
        else if (c.OpenPhase == CompactorOpenPhase.AfterStrapping) post = Math.Max(clampPost, (k.IsEnabled ? k.HoldTimeS : 0) + compOpen);
        else post = clampPost;
        return (pre, post);
    }

    public static StrategyComparison Compare(LineProject project, LineTopology topo, PackageType type)
    {
        var mode = project.StrappingStation.Strategy;
        var pb = BuildProblem(project, topo, type, out var err);
        if (pb == null)
        {
            var inf = StrappingPlan.Infeasible(StrappingOptimizer.AutomaticName, err ?? "errore");
            return new StrategyComparison
            {
                PackageTypeId = type.Id, Mode = mode, Automatic = new OptimizationResult(), Default = inf,
                Selected = inf, SelectionReason = $"Impossibile pianificare: {err}", Error = err
            };
        }

        var opt = new StrappingOptimizer();
        var auto = opt.Optimize(pb);
        var def = opt.DefaultPlan(pb);
        var manualCfg = project.StrappingStation.ManualPlans.FirstOrDefault(m => m.PackageTypeId == type.Id);
        StrappingPlan? manual = manualCfg != null ? opt.EvaluateManual(pb, manualCfg) : null;

        StrappingPlan selected;
        string? error = null;
        switch (mode)
        {
            case StrappingStrategyMode.Manual:
                selected = manual ?? StrappingPlan.Infeasible(StrappingOptimizer.ManualName, "nessun piano manuale definito per questa tipologia");
                break;
            case StrappingStrategyMode.Default:
                selected = def;
                break;
            default:
                selected = auto.Best ?? StrappingPlan.Infeasible(StrappingOptimizer.AutomaticName, "nessuna combinazione di legatrici ammissibile");
                break;
        }
        if (!selected.IsFeasible) error = selected.InfeasibleReason;

        return new StrategyComparison
        {
            PackageTypeId = type.Id,
            Mode = mode,
            Automatic = auto,
            Default = def,
            Manual = manual,
            Selected = selected,
            SelectionReason = BuildReason(mode, auto, def, manual, selected),
            Error = error
        };
    }

    private static string BuildReason(StrappingStrategyMode mode, OptimizationResult auto, StrappingPlan def, StrappingPlan? manual, StrappingPlan selected)
    {
        var ci = CultureInfo.InvariantCulture;
        string S(double v) => v.ToString("0.0", ci);
        var sb = new StringBuilder();
        var best = auto.Best;
        sb.AppendLine($"Combinazioni di legatrici valutate: {auto.EvaluatedCombinations}, ammissibili: {auto.FeasibleCombinations}.");
        foreach (var c in auto.Candidates.OrderBy(c => c.TotalTimeS))
            sb.AppendLine("  • " + c.Summary);

        if (best != null)
        {
            sb.AppendLine($"Ottimo: {string.Join("+", best.UsedStrapperIds)} con {best.Stops.Count} fermate e tempo stazione {S(best.TotalTimeS)} s.");
            var second = auto.Candidates.Where(c => c.IsFeasible && c != best).OrderBy(c => c.TotalTimeS).FirstOrDefault();
            if (second != null)
                sb.AppendLine($"La seconda migliore ({string.Join("+", second.UsedStrapperIds)}) richiede {S(second.TotalTimeS - best.TotalTimeS)} s in più.");
            if (best.ParallelStops > 0)
                sb.AppendLine($"Il vantaggio deriva da {best.ParallelStops} fermate con legature eseguite in parallelo: la distanza tra le legatrici coincide con la distanza tra le legature.");
            if (def.IsFeasible)
            {
                double gain = def.TotalTimeS - best.TotalTimeS;
                sb.AppendLine($"Rispetto alla predefinita ({S(def.TotalTimeS)} s) il risparmio è {S(gain)} s ({(def.TotalTimeS > 0 ? gain / def.TotalTimeS * 100 : 0).ToString("0", ci)}%).");
            }
            else sb.AppendLine($"La strategia predefinita non è ammissibile: {def.InfeasibleReason}.");
        }
        else sb.AppendLine("Nessuna combinazione ammissibile: verificare posizioni legatrici, lunghezza zona e pattern di legatura.");

        if (manual != null)
            sb.AppendLine(manual.IsFeasible && best != null
                ? $"Strategia manuale: {S(manual.TotalTimeS)} s ({(manual.TotalTimeS - best.TotalTimeS >= 0 ? "+" : "")}{S(manual.TotalTimeS - best.TotalTimeS)} s rispetto all'ottimo)."
                : $"Strategia manuale: {manual.Summary}");

        sb.Append(mode switch
        {
            StrappingStrategyMode.Manual => "Selezionata: MANUALE (scelta dell'utente).",
            StrappingStrategyMode.Default => "Selezionata: PREDEFINITA (scelta dell'utente).",
            _ => selected.IsFeasible ? "Selezionata: AUTOMATICA, perché ha il minor tempo stazione tra tutte le combinazioni ammissibili." : "Nessuna strategia selezionabile."
        });
        return sb.ToString();
    }
}
