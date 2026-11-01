using LineSimulator.Models;

namespace LineSimulator.Engine.Logic;

/// <summary>Ingressi leggibili dalle condizioni logiche (immagine di processo della scansione precedente).</summary>
public interface ILogicContext
{
    bool PhotocellOccupied(string id);
    bool InverterMoving(string id);
    bool ZoneOccupied(string inverterId);
    bool StrapperBusy(string id);
    bool StopperOccupied { get; }
    bool TransferAtHome { get; }
    bool AnyHeadInRange(double fromMm, double toMm);
    bool GapAheadLessThan(string inverterId, double gapMm);
    bool Signal(string name);
}

/// <summary>Richieste sull'inverter prodotte da regole e fotocellule.</summary>
public sealed class InverterRequests
{
    public bool Stop;
    public bool Low;
    public bool High;
    public readonly List<string> StopReasons = new();
    public readonly List<string> SpeedReasons = new();

    public void Clear()
    {
        Stop = Low = High = false;
        StopReasons.Clear();
        SpeedReasons.Clear();
    }
}

/// <summary>Valutatore delle condizioni AND/OR/NOT e delle regole.</summary>
public static class LogicEvaluator
{
    public static bool Evaluate(LogicCondition? c, ILogicContext ctx) => c switch
    {
        null => false,
        AndCondition a => a.Children.Count > 0 && a.Children.All(ch => Evaluate(ch, ctx)),
        OrCondition o => o.Children.Any(ch => Evaluate(ch, ctx)),
        NotCondition n => !Evaluate(n.Child, ctx),
        ConstantCondition k => k.Value,
        PhotocellCondition pc => ctx.PhotocellOccupied(pc.PhotocellId) == pc.Occupied,
        InverterRunningCondition ir => ctx.InverterMoving(ir.InverterId) == ir.Running,
        ZoneOccupiedCondition zo => ctx.ZoneOccupied(zo.InverterId),
        StrapperBusyCondition sb => ctx.StrapperBusy(sb.StrapperId),
        StopperOccupiedCondition => ctx.StopperOccupied,
        TransferAtHomeCondition => ctx.TransferAtHome,
        PackageHeadInRangeCondition hr => ctx.AnyHeadInRange(hr.FromMm, hr.ToMm),
        GapAheadLessThanCondition g => ctx.GapAheadLessThan(g.InverterId, g.GapMm),
        SignalCondition s => ctx.Signal(s.SignalName),
        _ => throw new NotSupportedException($"Condizione non supportata: {c.GetType().Name}")
    };

    /// <summary>
    /// Applica le regole: aggiorna le richieste per inverter e calcola i nuovi segnali.
    /// I segnali calcolati diventano visibili alla scansione successiva (doppio buffer).
    /// </summary>
    public static void ApplyRules(IEnumerable<LogicRule> rules, ILogicContext ctx,
        IDictionary<string, InverterRequests> requests, ISet<string> nextSignals)
    {
        foreach (var r in rules)
        {
            if (!r.IsEnabled) continue;
            if (!Evaluate(r.Condition, ctx)) continue;
            if (r.Action == RuleAction.SetSignal)
            {
                nextSignals.Add(r.SignalName);
                continue;
            }
            if (!requests.TryGetValue(r.TargetInverterId, out var req)) continue;
            string label = string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name;
            switch (r.Action)
            {
                case RuleAction.StopInverter: req.Stop = true; req.StopReasons.Add("Regola " + label); break;
                case RuleAction.SetLowSpeed: req.Low = true; req.SpeedReasons.Add("Regola " + label); break;
                case RuleAction.SetHighSpeed: req.High = true; req.SpeedReasons.Add("Regola " + label); break;
            }
        }
    }
}
