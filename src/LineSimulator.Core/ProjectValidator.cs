using System.Globalization;
using LineSimulator.Models;

namespace LineSimulator.Core;

public enum IssueSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, string? ComponentId = null)
{
    public override string ToString() => $"[{Severity}] {Code}: {Message}" + (ComponentId != null ? $" ({ComponentId})" : "");
}

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; } = new();
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);

    internal void Error(string code, string msg, string? id = null) => Issues.Add(new(IssueSeverity.Error, code, msg, id));
    internal void Warn(string code, string msg, string? id = null) => Issues.Add(new(IssueSeverity.Warning, code, msg, id));
    internal void Info(string code, string msg, string? id = null) => Issues.Add(new(IssueSeverity.Info, code, msg, id));
}

/// <summary>
/// Verifica di coerenza del progetto prima della simulazione. Gli errori impediscono l'avvio;
/// gli avvisi segnalano condizioni fisicamente rischiose (es. spazio di frenatura insufficiente).
/// </summary>
public static class ProjectValidator
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static ValidationReport Validate(LineProject p)
    {
        var r = new ValidationReport();
        if (p.SchemaVersion > LineProject.CurrentSchemaVersion)
            r.Error("SCHEMA", $"Versione schema {p.SchemaVersion} più recente di quella supportata ({LineProject.CurrentSchemaVersion}).");

        ValidateIds(p, r);
        ValidateInverters(p, r);
        ValidateSections(p, r);
        var topo = new LineTopology(p);
        ValidateZones(p, topo, r);
        ValidatePackages(p, r);
        ValidatePhotocells(p, topo, r);
        ValidateStrapping(p, topo, r);
        ValidateStopperAndTransfer(p, topo, r);
        ValidateInfeed(p, topo, r);
        ValidateRules(p, r);
        ValidateSimulation(p, r);
        return r;
    }

    private static void ValidateIds(LineProject p, ValidationReport r)
    {
        var all = new List<ComponentBase>();
        all.AddRange(p.Inverters); all.AddRange(p.Sections); all.AddRange(p.Photocells);
        all.AddRange(p.Strappers); all.AddRange(p.PackageTypes); all.AddRange(p.Rules);
        all.Add(p.Stopper); all.Add(p.Transfer);
        foreach (var c in all.Where(c => string.IsNullOrWhiteSpace(c.Id)))
            r.Error("ID_EMPTY", $"Componente '{c.Name}' senza identificativo.");
        foreach (var g in all.Where(c => !string.IsNullOrWhiteSpace(c.Id)).GroupBy(c => c.Id).Where(g => g.Count() > 1))
            r.Error("ID_DUP", $"Identificativo duplicato: {g.Key}.", g.Key);
    }

    private static void ValidateInverters(LineProject p, ValidationReport r)
    {
        foreach (var inv in p.Inverters)
        {
            if (inv.HighSpeed <= 0) r.Error("INV_SPEED", "Velocità alta deve essere > 0 m/s.", inv.Id);
            if (inv.LowSpeed <= 0) r.Error("INV_SPEED", "Velocità bassa deve essere > 0 m/s.", inv.Id);
            if (inv.LowSpeed > inv.HighSpeed) r.Error("INV_SPEED", "Velocità bassa maggiore della velocità alta.", inv.Id);
            if (inv.Acceleration <= 0) r.Error("INV_RAMP", "Accelerazione deve essere > 0 m/s².", inv.Id);
            if (inv.Deceleration <= 0) r.Error("INV_RAMP", "Decelerazione deve essere > 0 m/s².", inv.Id);
            if (!p.Sections.Any(s => s.InverterId == inv.Id))
                r.Warn("INV_UNUSED", "Inverter non associato ad alcuna sezione.", inv.Id);
            if (inv.Deceleration > p.Simulation.MaxFrictionDeceleration)
                r.Warn("INV_SLIP", $"Decelerazione {F(inv.Deceleration)} m/s² superiore al limite d'attrito ({F(p.Simulation.MaxFrictionDeceleration)}): il pacco slitterà.", inv.Id);
        }
    }

    private static void ValidateSections(LineProject p, ValidationReport r)
    {
        if (p.Sections.Count == 0) { r.Error("LINE_EMPTY", "La linea non contiene sezioni a rulli."); return; }
        foreach (var s in p.Sections)
        {
            if (s.LengthMm <= 0) r.Error("SEC_LEN", "Lunghezza sezione deve essere > 0.", s.Id);
            if (s.RollerPitchMm <= 0) r.Error("SEC_PITCH", "Passo rulli deve essere > 0.", s.Id);
            if (s.RollerCount < 2) r.Error("SEC_ROLLERS", "Servono almeno 2 rulli.", s.Id);
            else if ((s.RollerCount - 1) * s.RollerPitchMm > s.LengthMm + 1e-6)
                r.Error("SEC_ROLLERS", $"{s.RollerCount} rulli a passo {F(s.RollerPitchMm)} mm non stanno in {F(s.LengthMm)} mm.", s.Id);
            else if ((s.RollerCount - 1) * s.RollerPitchMm < s.LengthMm - 2 * s.RollerPitchMm)
                r.Warn("SEC_ROLLERS", "Numero rulli insufficiente a coprire la sezione al passo indicato.", s.Id);
            if (s.RollerPitchMm > 0 && s.RollerDiameterMm > s.RollerPitchMm)
                r.Error("SEC_ROLLERS", "Diametro rulli maggiore del passo: i rulli si compenetrano.", s.Id);
            if (s.Direction == TransportDirection.Reverse)
                r.Error("SEC_DIR", "Direzione inversa non supportata dal motore v1 (solo flusso in avanti).", s.Id);
            if (string.IsNullOrEmpty(s.InverterId) || !p.Inverters.Any(i => i.Id == s.InverterId))
                r.Error("SEC_INV", $"Inverter '{s.InverterId}' non trovato.", s.Id);
            if (!s.IsEnabled)
                r.Warn("SEC_OFF", "Sezione non operativa: i rulli restano fermi.", s.Id);
        }
        var ordered = p.Sections.OrderBy(s => s.StartMm).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            double gap = ordered[i].StartMm - ordered[i - 1].EndMm;
            if (gap < -0.5) r.Error("SEC_OVERLAP", $"Sezioni {ordered[i - 1].Id} e {ordered[i].Id} sovrapposte di {F(-gap)} mm.", ordered[i].Id);
            else if (gap > 0.5) r.Error("SEC_GAP", $"Interruzione di {F(gap)} mm tra {ordered[i - 1].Id} e {ordered[i].Id}.", ordered[i].Id);
        }
    }

    private static void ValidateZones(LineProject p, LineTopology topo, ValidationReport r)
    {
        foreach (var z in topo.Zones.Where(z => !z.IsContiguous))
            r.Error("ZONE_SPLIT", "Le sezioni di un inverter devono essere contigue (zona unica).", z.Id);
        for (int i = 1; i < topo.Zones.Count; i++)
            if (topo.Zones[i].StartM < topo.Zones[i - 1].EndM - 1e-6)
                r.Error("ZONE_INTERLEAVE", $"Le zone {topo.Zones[i - 1].Id} e {topo.Zones[i].Id} si intrecciano.", topo.Zones[i].Id);
    }

    private static void ValidatePackages(LineProject p, ValidationReport r)
    {
        if (p.PackageTypes.Count == 0) { r.Error("PKG_NONE", "Definire almeno una tipologia di pacco."); return; }
        foreach (var t in p.PackageTypes)
        {
            if (t.LengthMm <= 0) r.Error("PKG_LEN", "Lunghezza pacco deve essere > 0.", t.Id);
            if (t.WeightKg <= 0) r.Error("PKG_WEIGHT", "Peso pacco deve essere > 0.", t.Id);
            var pos = t.StrapPositionsMm.OrderBy(x => x).ToList();
            foreach (var x in pos)
                if (x < t.MinStrapDistanceFromEndsMm - 1e-6 || x > t.LengthMm - t.MinStrapDistanceFromEndsMm + 1e-6)
                    r.Error("STRAP_POS", $"Legatura a {F(x)} mm fuori dai limiti [{F(t.MinStrapDistanceFromEndsMm)}; {F(t.LengthMm - t.MinStrapDistanceFromEndsMm)}].", t.Id);
            for (int i = 1; i < pos.Count; i++)
                if (pos[i] - pos[i - 1] < t.MinDistanceBetweenStrapsMm - 1e-6)
                    r.Error("STRAP_GAP", $"Legature a {F(pos[i - 1])} e {F(pos[i])} mm più vicine del minimo {F(t.MinDistanceBetweenStrapsMm)} mm.", t.Id);
            if (pos.Count > 10) r.Warn("STRAP_MANY", "Più di 10 legature: l'ottimizzazione esatta può richiedere tempo.", t.Id);
        }
    }

    private static void ValidatePhotocells(LineProject p, LineTopology topo, ValidationReport r)
    {
        foreach (var pc in p.Photocells)
        {
            double x = Units.MmToM(pc.PositionMm);
            if (x < topo.LineStartM || x > topo.LineEndM)
                r.Error("PC_POS", $"Fotocellula fuori linea ({F(pc.PositionMm)} mm).", pc.Id);
            if (pc.ResponseTimeMs < 0) r.Error("PC_RESP", "Tempo di risposta negativo.", pc.Id);
            if (pc.Action == PhotocellAction.None) continue;
            var zone = topo.Zone(pc.ControlledInverterId);
            if (zone == null) { r.Error("PC_INV", $"Inverter comandato '{pc.ControlledInverterId}' non trovato.", pc.Id); continue; }
            if (pc.Action == PhotocellAction.Accumulation)
            {
                if (pc.Activation != PhotocellActivation.WhenOccupied)
                    r.Error("PC_ACC", "La fotocellula di accumulo deve attivarsi con pacco presente.", pc.Id);
                if (x < zone.StartM || x > zone.EndM)
                    r.Warn("PC_ACC_ZONE", $"Fotocellula di accumulo fuori dalla zona dell'inverter {zone.Id}.", pc.Id);
                double v = zone.Inverter.HighSpeed;
                double s = Kinematics.StoppingDistance(v, Units.MsToS(pc.ResponseTimeMs), zone.Inverter.Deceleration);
                double headStop = x + s;
                double margin = zone.EndM - headStop;
                if (margin < 0)
                    r.Error("PC_BRAKE", $"Spazio di arresto insufficiente: da {F(v)} m/s servono {F(s * 1000)} mm (risposta + frenatura), ma la zona finisce {F((zone.EndM - x) * 1000)} mm dopo la fotocellula: il pacco invade la zona a valle.", pc.Id);
                else if (margin < Units.MmToM(p.Simulation.MinSafetyDistanceMm))
                    r.Warn("PC_BRAKE_MARGIN", $"Margine di arresto {F(margin * 1000)} mm inferiore alla distanza di sicurezza {F(p.Simulation.MinSafetyDistanceMm)} mm.", pc.Id);
            }
        }
    }

    private static void ValidateStrapping(LineProject p, LineTopology topo, ValidationReport r)
    {
        if (p.Strappers.Count > LineProject.MaxStrappers)
            r.Error("STR_COUNT", $"Massimo {LineProject.MaxStrappers} legatrici.");
        var st = p.StrappingStation;
        if (!st.IsEnabled) { r.Info("STATION_OFF", "Stazione di legatura disabilitata: i pacchi transitano senza legatura."); return; }
        if (p.Strappers.Count(s => s.IsEnabled) == 0) r.Error("STR_NONE", "Nessuna legatrice abilitata.");
        var zone = topo.Zone(st.InverterId);
        if (zone == null) { r.Error("STATION_INV", $"Inverter della zona di legatura '{st.InverterId}' non trovato."); return; }
        if (topo.StopperZone != null && zone == topo.StopperZone)
            r.Error("STATION_STOPPER", "La zona di legatura non può coincidere con la zona dello stopper.");
        if (st.PositioningSpeed <= 0 || st.PositioningSpeed > zone.Inverter.HighSpeed + 1e-9)
            r.Error("STATION_SPEED", "Velocità di posizionamento deve essere > 0 e ≤ velocità alta dell'inverter.");
        double maxLen = p.PackageTypes.Count > 0 ? p.PackageTypes.Max(t => t.LengthMm) : 0;
        if (Units.MToMm(zone.LengthM) < maxLen)
            r.Error("STATION_LEN", $"Zona di legatura ({F(Units.MToMm(zone.LengthM))} mm) più corta del pacco più lungo ({F(maxLen)} mm).");

        var en = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).ToList();
        for (int i = 1; i < en.Count; i++)
        {
            double clear = (en[i].PositionMm - en[i].FootprintLengthMm / 2) - (en[i - 1].PositionMm + en[i - 1].FootprintLengthMm / 2);
            if (clear < 0)
                r.Error("STR_INTERF", $"Ingombri delle legatrici {en[i - 1].Id} e {en[i].Id} interferiscono ({F(-clear)} mm).", en[i].Id);
        }
        foreach (var s in p.Strappers)
        {
            double x = Units.MmToM(s.PositionMm);
            if (x < zone.StartM || x > zone.EndM)
                r.Error("STR_POS", $"Legatrice fuori dalla zona di legatura {zone.Id}.", s.Id);
            if (s.PreparationTimeS < 0 || s.StrappingTimeS <= 0 || s.ReturnTimeS < 0)
                r.Error("STR_TIME", "Tempi legatrice non validi (legatura > 0, altri ≥ 0).", s.Id);
        }
    }

    private static void ValidateStopperAndTransfer(LineProject p, LineTopology topo, ValidationReport r)
    {
        var stp = p.Stopper;
        double xs = Units.MmToM(stp.PositionMm);
        if (topo.StopperZone == null)
            r.Error("STP_POS", "Lo stopper deve trovarsi all'interno di una sezione a rulli.", stp.Id);
        else if (topo.Downstream(topo.StopperZone) != null)
            r.Warn("STP_NOT_LAST", "Esistono zone a valle dello stopper: non saranno mai raggiunte.", stp.Id);
        if (!stp.IsRaised)
            r.Error("STP_LOWERED", "Lo stopper deve essere sollevato: il transito a stopper abbassato non è supportato in v1.", stp.Id);
        if (stp.AlignmentTimeS < 0 || stp.HeadingTimeS < 0) r.Error("STP_TIME", "Tempi stopper negativi.", stp.Id);
        if (stp.AlignmentRollerSpeed < 0) r.Error("STP_SPEED", "Velocità di allineamento negativa.", stp.Id);

        if (topo.StopperZone != null)
        {
            var inv = topo.StopperZone.Inverter;
            bool slowed = p.Photocells.Any(pc => pc.ControlledInverterId == inv.Id && pc.Action == PhotocellAction.SlowDown)
                       || p.Rules.Any(rule => rule.IsEnabled && rule.TargetInverterId == inv.Id && rule.Action == RuleAction.SetLowSpeed)
                       || inv.DefaultSpeed == SpeedLevel.Low;
            double approach = slowed ? inv.LowSpeed : inv.HighSpeed;
            if (approach > stp.MaxImpactSpeed + 1e-9)
                r.Warn("STP_IMPACT", $"Velocità di arrivo allo stopper {F(approach)} m/s > massima ammessa {F(stp.MaxImpactSpeed)} m/s: prevedere rallentamento.", stp.Id);
            double maxLen = p.PackageTypes.Count > 0 ? Units.MmToM(p.PackageTypes.Max(t => t.LengthMm)) : 0;
            if (xs - maxLen < topo.StopperZone.StartM - 1e-6)
                r.Warn("STP_ZONE_LEN", "Il pacco più lungo allo stopper occupa anche la zona a monte.", stp.Id);
        }

        var tr = p.Transfer;
        if (tr.LiftTimeS < 0 || tr.LowerTimeS < 0 || tr.DepositTimeS < 0) r.Error("TRF_TIME", "Tempi trasferitore negativi.", tr.Id);
        if (double.IsInfinity(tr.EffectiveTranslationTimeS)) r.Error("TRF_SPEED", "Velocità di traslazione nulla e tempo non imposto.", tr.Id);
        double minLen = p.PackageTypes.Count > 0 ? p.PackageTypes.Min(t => t.LengthMm) : 0;
        if (tr.EndMm < stp.PositionMm - minLen || tr.PositionMm > stp.PositionMm)
            r.Error("TRF_POS", "Il trasferitore non si trova sotto il pacco in battuta allo stopper.", tr.Id);
    }

    private static void ValidateInfeed(LineProject p, LineTopology topo, ValidationReport r)
    {
        var f = p.Infeed;
        if (f.PackageCount <= 0) r.Error("INF_COUNT", "Numero pacchi deve essere > 0.");
        if (f.IntervalS < 0) r.Error("INF_INT", "Intervallo di ingresso negativo.");
        if (f.MinimumGapMm < 0) r.Error("INF_GAP", "Distanza minima tra pacchi negativa.");
        foreach (var id in f.TypeSequence.Where(id => !p.PackageTypes.Any(t => t.Id == id)))
            r.Error("INF_TYPE", $"Tipologia '{id}' nella sequenza non definita.");
        if (f.MinimumGapMm < p.Simulation.MinSafetyDistanceMm)
            r.Warn("INF_GAP_SAFETY", "Distanza iniziale tra pacchi inferiore alla distanza minima di sicurezza.");
        double maxLen = p.PackageTypes.Count > 0 ? Units.MmToM(p.PackageTypes.Max(t => t.LengthMm)) : 0;
        if (topo.LineEndM - topo.LineStartM < maxLen)
            r.Error("LINE_SHORT", "Linea più corta del pacco più lungo.");
    }

    private static void ValidateRules(LineProject p, ValidationReport r)
    {
        foreach (var rule in p.Rules.Where(x => x.IsEnabled))
        {
            if (rule.Action != RuleAction.SetSignal && !p.Inverters.Any(i => i.Id == rule.TargetInverterId))
                r.Error("RULE_TARGET", $"Inverter destinazione '{rule.TargetInverterId}' non trovato.", rule.Id);
            if (rule.Action == RuleAction.SetSignal && string.IsNullOrWhiteSpace(rule.SignalName))
                r.Error("RULE_SIGNAL", "Nome segnale mancante.", rule.Id);
            foreach (var msg in CheckCondition(p, rule.Condition))
                r.Error("RULE_REF", msg, rule.Id);
        }
    }

    private static IEnumerable<string> CheckCondition(LineProject p, LogicCondition? c)
    {
        switch (c)
        {
            case null: yield return "Condizione mancante."; break;
            case AndCondition a: foreach (var ch in a.Children) foreach (var m in CheckCondition(p, ch)) yield return m; break;
            case OrCondition o: foreach (var ch in o.Children) foreach (var m in CheckCondition(p, ch)) yield return m; break;
            case NotCondition n: foreach (var m in CheckCondition(p, n.Child)) yield return m; break;
            case PhotocellCondition pc when !p.Photocells.Any(x => x.Id == pc.PhotocellId):
                yield return $"Fotocellula '{pc.PhotocellId}' non trovata."; break;
            case InverterRunningCondition ir when !p.Inverters.Any(x => x.Id == ir.InverterId):
                yield return $"Inverter '{ir.InverterId}' non trovato."; break;
            case ZoneOccupiedCondition zo when !p.Inverters.Any(x => x.Id == zo.InverterId):
                yield return $"Inverter '{zo.InverterId}' non trovato."; break;
            case GapAheadLessThanCondition g when !p.Inverters.Any(x => x.Id == g.InverterId):
                yield return $"Inverter '{g.InverterId}' non trovato."; break;
            case StrapperBusyCondition sb when !p.Strappers.Any(x => x.Id == sb.StrapperId):
                yield return $"Legatrice '{sb.StrapperId}' non trovata."; break;
        }
    }

    private static void ValidateSimulation(LineProject p, ValidationReport r)
    {
        var s = p.Simulation;
        if (s.TimeStepMs <= 0 || s.TimeStepMs > 100) r.Error("SIM_DT", "Passo di integrazione deve essere in (0; 100] ms.");
        if (s.MaxSimulationTimeS <= 0) r.Error("SIM_MAX", "Durata massima deve essere > 0.");
        if (s.RecordIntervalMs < s.TimeStepMs) r.Warn("SIM_REC", "Intervallo di registrazione minore del passo: verrà usato il passo.");
        if (s.MaxFrictionDeceleration <= 0) r.Error("SIM_FRICTION", "Decelerazione d'attrito deve essere > 0.");
        if (s.DeadlockTimeoutS <= 0) r.Error("SIM_DEADLOCK", "Timeout deadlock deve essere > 0.");
    }
}
