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
        ValidateProcessDevices(p, topo, r);
        return r;
    }

    private static void ValidateIds(LineProject p, ValidationReport r)
    {
        var all = new List<ComponentBase>();
        all.AddRange(p.Sections); all.AddRange(p.Photocells);
        all.AddRange(p.Strappers); all.AddRange(p.PackageTypes); all.AddRange(p.Rules);
        all.Add(p.Stopper); all.Add(p.Transfer);
        if (p.BundleClamp.IsEnabled) all.Add(p.BundleClamp);
        if (p.Compactor.IsEnabled) all.Add(p.Compactor);
        if (p.Rejector.IsEnabled) all.Add(p.Rejector);
        all.AddRange(p.IntermediateStops); all.AddRange(p.Scales);
        foreach (var c in all.Where(c => string.IsNullOrWhiteSpace(c.Id)))
            r.Error("ID_EMPTY", $"Componente '{c.Name}' senza identificativo.");
        foreach (var g in all.Where(c => !string.IsNullOrWhiteSpace(c.Id)).GroupBy(c => c.Id).Where(g => g.Count() > 1))
            r.Error("ID_DUP", $"Identificativo duplicato: {g.Key}.", g.Key);
    }

    private static void ValidateInverters(LineProject p, ValidationReport r)
    {
        // Parametri inverter di ogni modulo di via a rulli
        foreach (var inv in p.Sections)
        {
            if (inv.HighSpeed <= 0) r.Error("INV_SPEED", "Velocità alta deve essere > 0 m/s.", inv.Id);
            if (inv.LowSpeed <= 0) r.Error("INV_SPEED", "Velocità bassa deve essere > 0 m/s.", inv.Id);
            if (inv.LowSpeed > inv.HighSpeed) r.Error("INV_SPEED", "Velocità bassa maggiore della velocità alta.", inv.Id);
            if (inv.Acceleration <= 0) r.Error("INV_RAMP", "Accelerazione deve essere > 0 m/s².", inv.Id);
            if (inv.Deceleration <= 0) r.Error("INV_RAMP", "Decelerazione deve essere > 0 m/s².", inv.Id);
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
            if (!s.IsEnabled)
                r.Warn("SEC_OFF", "Modulo non operativo: inverter fermo, i rulli restano fermi.", s.Id);
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
            r.Error("ZONE_SPLIT", "I moduli di una zona di comando (stesso inverter) devono essere contigui.", z.Id);
        foreach (var z in topo.Zones.Where(z => z.Sections.Count > 1))
        {
            var a = z.Sections[0];
            foreach (var b in z.Sections.Skip(1))
                if (b.HighSpeed != a.HighSpeed || b.LowSpeed != a.LowSpeed || b.Acceleration != a.Acceleration || b.Deceleration != a.Deceleration)
                    r.Warn("ZONE_DRIVE", $"Il modulo {b.Id} ha parametri di azionamento diversi da {a.Id}: la zona {z.Id} usa quelli di {a.Id} (un solo inverter).", b.Id);
            if (p.Sections.Any(s => s.Id == z.Id && !z.Sections.Contains(s)))
                r.Error("ZONE_ID", $"L'ID di zona {z.Id} coincide con quello di un modulo di un'altra zona.", z.Id);
        }
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
            if (zone == null) { r.Error("PC_INV", $"Modulo comandato '{pc.ControlledInverterId}' non trovato.", pc.Id); continue; }
            if (pc.Action == PhotocellAction.Accumulation)
            {
                if (pc.Activation != PhotocellActivation.WhenOccupied)
                    r.Error("PC_ACC", "La fotocellula di accumulo deve attivarsi con pacco presente.", pc.Id);
                if (pc.DetectionMode != PhotocellDetectionMode.Presence)
                    r.Error("PC_ACC_MODE", "La fotocellula di accumulo deve usare la logica di presenza.", pc.Id);
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
        if (zone == null) { r.Error("STATION_INV", $"Modulo della zona di legatura '{st.InverterId}' non trovato."); return; }
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
            bool slowed = p.Photocells.Any(pc => topo.ResolveZoneId(pc.ControlledInverterId) == inv.Id && pc.Action == PhotocellAction.SlowDown)
                       || p.Rules.Any(rule => rule.IsEnabled && topo.ResolveZoneId(rule.TargetInverterId) == inv.Id && rule.Action == RuleAction.SetLowSpeed)
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
            if (rule.Action != RuleAction.SetSignal && !ZoneOrModuleExists(p, rule.TargetInverterId))
                r.Error("RULE_TARGET", $"Modulo destinazione '{rule.TargetInverterId}' non trovato.", rule.Id);
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
            case InverterRunningCondition ir when !ZoneOrModuleExists(p, ir.InverterId):
                yield return $"Modulo '{ir.InverterId}' non trovato."; break;
            case ZoneOccupiedCondition zo when !ZoneOrModuleExists(p, zo.InverterId):
                yield return $"Modulo '{zo.InverterId}' non trovato."; break;
            case GapAheadLessThanCondition g when !ZoneOrModuleExists(p, g.InverterId):
                yield return $"Modulo '{g.InverterId}' non trovato."; break;
            case StrapperBusyCondition sb when !p.Strappers.Any(x => x.Id == sb.StrapperId):
                yield return $"Legatrice '{sb.StrapperId}' non trovata."; break;
            case DeviceStateCondition d when !DeviceIds(p).Contains(d.DeviceId):
                yield return $"Dispositivo '{d.DeviceId}' non trovato."; break;
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

    private static bool ZoneOrModuleExists(LineProject p, string id) =>
        !string.IsNullOrEmpty(id) && p.Sections.Any(s => s.Id == id || s.EffectiveZoneId == id);

    private static HashSet<string> DeviceIds(LineProject p)
    {
        var ids = new HashSet<string>(p.Strappers.Select(x => x.Id)) { p.Stopper.Id, p.Transfer.Id, "STATION" };
        if (p.BundleClamp.IsEnabled) ids.Add(p.BundleClamp.Id);
        if (p.Compactor.IsEnabled) ids.Add(p.Compactor.Id);
        if (p.Rejector.IsEnabled) ids.Add(p.Rejector.Id);
        foreach (var s in p.IntermediateStops) ids.Add(s.Id);
        foreach (var s in p.Scales) ids.Add(s.Id);
        return ids;
    }

    private static void CheckDeviceCommon(LineProject p, SequenceDeviceConfig d, ValidationReport r, double closeTime)
    {
        foreach (var (text, label) in new[] { (d.StartConsent, "consenso di avvio"), (d.StopCondition, "condizione di arresto") })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            LogicCondition? c = null;
            try { c = ConditionText.Parse(text); }
            catch (FormatException ex) { r.Error("DEV_COND", $"Errore nella {label}: {ex.Message}", d.Id); }
            if (c != null) foreach (var m in CheckCondition(p, c)) r.Error("DEV_COND", $"{label}: {m}", d.Id);
        }
        if (d.ConfirmDelayS < 0) r.Error("DEV_TIME", "Ritardo di conferma negativo.", d.Id);
        if (d.ConfirmTimeoutS <= closeTime + d.ConfirmDelayS)
            r.Error("DEV_TIMEOUT", $"Timeout conferma ({F(d.ConfirmTimeoutS)} s) non superiore a tempo movimento + ritardo conferma ({F(closeTime + d.ConfirmDelayS)} s).", d.Id);
        if (d.FaultResetTimeS < 0) r.Error("DEV_TIME", "Tempo di ripristino negativo.", d.Id);
        if (d.SimulatedFaultAtCycle < 0) r.Error("DEV_FAULT", "Ciclo di guasto simulato negativo.", d.Id);
        if (d.SimulatedFaultAtCycle > 0) r.Info("DEV_FAULT_SIM", $"Guasto simulato attivo al ciclo {d.SimulatedFaultAtCycle}.", d.Id);
    }

    /// <summary>Fotocellula di ingresso, dispositivi di processo, fermi intermedi, trasferitore multi-pacco, bilici.</summary>
    private static void ValidateProcessDevices(LineProject p, LineTopology topo, ValidationReport r)
    {
        // Fotocellula di ingresso obbligatoria
        var entries = p.Photocells.Where(pc => pc.IsEntryPhotocell && pc.IsEnabled).ToList();
        if (entries.Count == 0)
            r.Error("PC_ENTRY", "Manca la fotocellula di ingresso (obbligatoria): marcare una fotocellula come 'Fotocellula di ingresso'.");
        else
        {
            if (entries.Count > 1) r.Warn("PC_ENTRY_MULTI", "Più fotocellule di ingresso: l'identificazione avviene alla prima che rileva il pacco.");
            var first = topo.Zones.FirstOrDefault();
            double minLen = p.PackageTypes.Count > 0 ? p.PackageTypes.Min(t => t.LengthMm) : 0;
            foreach (var e in entries)
            {
                if (e.DetectionMode != PhotocellDetectionMode.Presence)
                    r.Error("PC_ENTRY_MODE", "La fotocellula di ingresso deve usare la logica di presenza.", e.Id);
                if (first != null && Units.MmToM(e.PositionMm) > first.EndM)
                    r.Warn("PC_ENTRY_POS", "La fotocellula di ingresso non si trova nella prima zona della linea.", e.Id);
                if (e.PositionMm - Units.MToMm(topo.LineStartM) > minLen)
                    r.Warn("PC_ENTRY_FAR", $"La fotocellula di ingresso è oltre la lunghezza del pacco più corto ({F(minLen)} mm) dall'inizio linea: l'identificazione avviene dopo l'ingresso.", e.Id);
            }
        }

        var st = p.StrappingStation;
        var stationZone = st.IsEnabled ? topo.Zone(st.InverterId) : null;

        // Stringifascio KNOF
        var k = p.BundleClamp;
        if (k.IsEnabled)
        {
            if (stationZone == null) r.Warn("KNOF_STATION", "Stringifascio inattivo: nessuna legatrice abilitata (stazione di legatura non attiva).", k.Id);
            else
            {
                double x = Units.MmToM(k.PositionMm);
                if (x < stationZone.StartM || x > stationZone.EndM) r.Error("KNOF_POS", $"Stringifascio fuori dalla zona di legatura {stationZone.Id}.", k.Id);
                var firstStr = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).FirstOrDefault();
                if (firstStr != null && k.PositionMm > firstStr.PositionMm)
                    r.Warn("KNOF_ENTRY", "Lo stringifascio dovrebbe essere installato in ingresso alla legatrice (a monte della prima legatrice).", k.Id);
            }
            if (k.CloseTimeS < 0 || k.OpenTimeS < 0 || k.HoldTimeS < 0) r.Error("KNOF_TIME", "Tempi stringifascio negativi.", k.Id);
            CheckDeviceCommon(p, k, r, k.CloseTimeS);
        }

        // Compattatore laterale
        var c = p.Compactor;
        if (c.IsEnabled)
        {
            if (stationZone == null) r.Warn("CMP_STATION", "Compattatore inattivo: nessuna legatrice abilitata (stazione di legatura non attiva).", c.Id);
            else
            {
                double x = Units.MmToM(c.PositionMm);
                if (x < stationZone.StartM || x > stationZone.EndM) r.Error("CMP_POS", $"Compattatore fuori dalla zona di legatura {stationZone.Id}.", c.Id);
            }
            if (c.CloseTimeS < 0 || c.OpenTimeS < 0 || c.HoldTimeS < 0) r.Error("CMP_TIME", "Tempi compattatore negativi.", c.Id);
            if (c.OpenPhase == CompactorOpenPhase.AfterBundleClampClosed && !k.IsEnabled)
                r.Error("CMP_PHASE", "Apertura 'dopo chiusura stringifascio' richiede lo stringifascio abilitato.", c.Id);
            CheckDeviceCommon(p, c, r, c.CloseTimeS);
        }

        // Rigettatrice SMA
        var sma = p.Rejector;
        if (sma.IsEnabled)
        {
            var z = topo.ZoneAt(Units.MmToM(sma.PositionMm + sma.LengthMm / 2));
            if (z == null) r.Error("SMA_POS", "Rigettatrice fuori dalla linea.", sma.Id);
            else
            {
                if (z.Kind != ZoneKind.Transport) r.Error("SMA_ZONE", $"La rigettatrice deve trovarsi in una zona di trasporto (non legatura/stopper): {z.Id}.", sma.Id);
                if (Units.MmToM(sma.PositionMm) < z.StartM - 1e-6 || Units.MmToM(sma.PositionMm + sma.LengthMm) > z.EndM + 1e-6)
                    r.Warn("SMA_SPAN", $"Il campo di spinta esce dalla zona {z.Id}.", sma.Id);
            }
            if (sma.LengthMm <= 0) r.Error("SMA_LEN", "Lunghezza campo di spinta deve essere > 0.", sma.Id);
            if (sma.EjectTimeS <= 0 || sma.ReturnTimeS < 0 || sma.RestartDelayS < 0) r.Error("SMA_TIME", "Tempi rigettatrice non validi.", sma.Id);
            try { sma.ParseRejectNumbers(); }
            catch (FormatException ex) { r.Error("SMA_LIST", ex.Message, sma.Id); }
            foreach (var id in sma.RejectTypeIds.Where(id => !p.PackageTypes.Any(t => t.Id == id)))
                r.Error("SMA_TYPE", $"Tipologia da rigettare '{id}' non definita.", sma.Id);
            foreach (var t in p.PackageTypes.Where(t => t.LengthMm > sma.LengthMm))
                r.Warn("SMA_PKG_LEN", $"Pacco {t.Id} più lungo del campo di spinta.", sma.Id);
            CheckDeviceCommon(p, sma, r, sma.EjectTimeS);
        }

        // Fermi intermedi e trasferitore multi-pacco
        var stops = p.IntermediateStops.Where(s => s.IsEnabled).ToList();
        foreach (var s in stops)
        {
            double x = Units.MmToM(s.PositionMm);
            var sz = topo.StopperZone;
            if (sz == null || x < sz.StartM || x > sz.EndM)
                r.Error("STOP_ZONE", "Il fermo intermedio deve trovarsi nella zona dello stopper.", s.Id);
            if (s.PositionMm >= p.Stopper.PositionMm) r.Error("STOP_POS", "Il fermo intermedio deve essere a monte dello stopper finale.", s.Id);
            if (s.PositionMm < p.Transfer.PositionMm || s.PositionMm > p.Transfer.EndMm)
                r.Warn("STOP_TRF", "Il fermo intermedio è fuori dal campo del trasferitore.", s.Id);
            if (s.AlignmentTimeS < 0) r.Error("STOP_TIME", "Tempo di intestatura negativo.", s.Id);
        }
        foreach (var g in stops.GroupBy(s => Math.Round(s.PositionMm)).Where(g => g.Count() > 1))
            r.Error("STOP_DUP", $"Più fermi nella stessa posizione ({F(g.Key)} mm).");
        var tr = p.Transfer;
        if (stops.Count == 0 && p.Simulation.Banking == AccumulationMode.DoubleBanking)
            r.Warn("BANKING_DOUBLE", "Double banking scelto ma nessun fermo intermedio scomparente abilitato: si simula in single banking.", tr.Id);
        if (stops.Count == 0 && p.Simulation.Heading == StopReference.Tail)
            r.Warn("HEADING_TAIL", "Intestatura di coda scelta ma nessun fermo intermedio scomparente abilitato: si simula con intestatura di testa.", tr.Id);
        if (tr.BatchWaitTimeoutS < 0) r.Error("TRF_BATCH", "Attesa gruppo negativa.", tr.Id);
        if (stops.Count > 0 && p.PackageTypes.Count > 0 && p.Simulation.Banking == AccumulationMode.DoubleBanking)
        {
            double minLen = p.PackageTypes.Min(t => t.LengthMm);
            var first = stops.OrderByDescending(s => s.PositionMm).First();
            double head = p.Simulation.Heading == StopReference.Head ? first.PositionMm : first.PositionMm + minLen + first.TailOvershootMm;
            if (head > p.Stopper.PositionMm - minLen - p.Simulation.MinSafetyDistanceMm + 1e-6)
                r.Warn("STOP_OVERLAP", $"Con il pacco più corto ({F(minLen)} mm) il fermo {first.Id} si sovrappone al pacco allo stopper: verrà usato solo se c'è spazio.", first.Id);
        }

        // Bilici
        var scales = p.Scales.Where(s => s.IsEnabled).OrderBy(s => s.StartMm).ToList();
        foreach (var b in scales)
        {
            if (b.LengthMm <= 0) r.Error("SCALE_LEN", "Lunghezza bilico deve essere > 0.", b.Id);
            if (b.StartMm < tr.PositionMm - 1 || b.EndMm > tr.EndMm + 500)
                r.Warn("SCALE_TRF", "Il bilico non è allineato al campo di deposito del trasferitore.", b.Id);
            if (b.StandardHeadPositionMm > 0 && (b.StandardHeadPositionMm < b.StartMm || b.StandardHeadPositionMm > b.EndMm))
                r.Error("SCALE_STD", "Posizione standard fuori dal bilico.", b.Id);
            if (b.IntermediateHeadPositionMm > 0 && (b.IntermediateHeadPositionMm < b.StartMm || b.IntermediateHeadPositionMm > b.EndMm))
                r.Error("SCALE_INT", "Posizione intermedia fuori dal bilico.", b.Id);
            if (b.PositioningSpeed <= 0 || b.Acceleration <= 0) r.Error("SCALE_SPEED", "Velocità e accelerazione del bilico devono essere > 0.", b.Id);
            if (b.StabilizationTimeS < 0 || b.AcquisitionTimeS < 0 || b.ReleaseTimeS < 0) r.Error("SCALE_TIME", "Tempi bilico negativi.", b.Id);
            if (b.ResolutionKg <= 0) r.Error("SCALE_RES", "Risoluzione deve essere > 0.", b.Id);
            CheckDeviceCommon(p, b, r, b.StabilizationTimeS);
        }
        for (int i = 1; i < scales.Count; i++)
            if (scales[i].StartMm < scales[i - 1].EndMm - 1e-6)
                r.Error("SCALE_OVERLAP", $"Bilici {scales[i - 1].Id} e {scales[i].Id} sovrapposti.", scales[i].Id);

        try { ProcessText.ParseWeights(p.Infeed.WeightOverrides); }
        catch (FormatException ex) { r.Error("INF_WEIGHT", ex.Message); }

        ValidateDrivesAndSensors(p, topo, r);
    }

    /// <summary>Arresto automatico/ripartenza per fotocellula, sensori di processo, tipo di legatura e macchine.</summary>
    private static void ValidateDrivesAndSensors(LineProject p, LineTopology topo, ValidationReport r)
    {
        var pcs = p.Photocells.Where(x => x.IsEnabled).ToList();
        foreach (var z in topo.Zones)
        {
            if (!z.Inverter.AutoStopWhenEmpty) continue;
            var own = pcs.Where(x => Units.MmToM(x.PositionMm) >= z.StartM - 1e-9 && Units.MmToM(x.PositionMm) <= z.EndM + 1e-9).ToList();
            if (own.Count == 0)
                r.Warn("ZONE_NO_PC", $"Zona {z.Id} senza fotocellule: l'arresto automatico (corsa a vuoto pari alla lunghezza) non può rilevare i pacchi.", z.Id);
            var restart = pcs.Where(x => Units.MmToM(x.PositionMm) < z.StartM - 1e-9).OrderByDescending(x => x.PositionMm).FirstOrDefault();
            var up = topo.Upstream(z);
            if (restart != null && up != null)
            {
                double dist = z.StartM - Units.MmToM(restart.PositionMm);
                double tAcc = z.Inverter.Acceleration > 0 ? z.Inverter.HighSpeed / z.Inverter.Acceleration : 0;
                double need = up.Inverter.HighSpeed * (tAcc + Units.MsToS(restart.ResponseTimeMs));
                if (dist < need - 1e-9)
                    r.Warn("ZONE_RESTART", $"Fotocellula di ripartenza {restart.Id} a {F(dist * 1000)} mm dall'inizio della zona {z.Id}: servono {F(need * 1000)} mm perché la zona sia a velocità all'arrivo del pacco.", z.Id);
            }
        }
        if (!p.Photocells.Any(x => x.Purpose == PhotocellPurpose.TransferStop))
            r.Warn("PC_TRF_STOP", $"Manca la fotocellula di arresto davanti al trasferitore (distanza consigliata {F(ProjectNormalizer.TransferStopDistanceMm(p))} mm).", p.Transfer.Id);
        foreach (var t in p.Photocells.Where(x => x.Purpose == PhotocellPurpose.TransferStop))
        {
            double d = p.Transfer.PositionMm - t.PositionMm;
            if (d < ProjectNormalizer.TransferStopDistanceMm(p) - 1)
                r.Warn("PC_TRF_DIST", $"Fotocellula di arresto a {F(d)} mm dal trasferitore: la regola richiede almeno {F(ProjectNormalizer.TransferStopDistanceMm(p))} mm (1,5 s × velocità massima + margine).", t.Id);
            if (topo.Zone(t.ControlledInverterId) == null)
                r.Error("PC_TRF_ZONE", $"Fotocellula di arresto davanti al trasferitore senza zona comandata valida ('{t.ControlledInverterId}').", t.Id);
        }
        var first = ProjectNormalizer.FirstStrappingMachine(p);
        if (first != null)
        {
            var refPc = p.Photocells.FirstOrDefault(x => x.Purpose == PhotocellPurpose.StrappingReference);
            if (refPc == null || !p.StrappingEncoder.IsEnabled)
                r.Warn("ENC_MISSING", "Manca l'encoder incrementale con la fotocellula di riferimento prima della prima macchina di legatura.");
            else
            {
                if (refPc.PositionMm >= p.StrappingEncoder.PositionMm)
                    r.Warn("ENC_ORDER", "La fotocellula di riferimento deve trovarsi prima (a monte) dell'encoder.", refPc.Id);
                if (p.StrappingEncoder.PositionMm >= first.PositionMm)
                    r.Warn("ENC_POS", $"L'encoder deve trovarsi a monte della prima macchina di legatura {first.Id}.", p.StrappingEncoder.Id);
            }
            foreach (var s in p.Strappers)
                if (s.ArmingTimeS < 0) r.Error("STR_ARM", "Tempo di carica filo / riarmo negativo.", s.Id);
            foreach (var t in p.PackageTypes.Where(t => t.StrapPositionsMm.Count > 0))
            {
                var need = t.BindingType == BindingType.SteelStrap ? StrapperMachineType.SMA : StrapperMachineType.KNCA;
                if (!p.Strappers.Any(s => s.IsEnabled && s.MachineType == need))
                    r.Error("STR_TYPE", $"Il pacco {t.Id} richiede {(need == StrapperMachineType.SMA ? "reggettature (SMA)" : "legature a filo (KNCA)")}, ma nella stazione non c'è una macchina di quel tipo abilitata.", t.Id);
            }
        }
    }
}
