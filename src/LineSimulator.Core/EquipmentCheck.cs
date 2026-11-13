using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Esito di una regola di controllo degli equipaggiamenti.</summary>
public sealed record EquipmentCheckItem(string Code, string Message, bool Fixed, bool IsProblem);

/// <summary>
/// Controllo automatico degli equipaggiamenti con regole (es. la prima fotocellula deve esserci ed essere nella posizione giusta).
/// Le correzioni sicure vengono applicate al progetto e riportate nell'elenco.
/// </summary>
public static class EquipmentCheck
{
    public const string EntryName = "FC1 (ingresso identificazione)";

    public static List<EquipmentCheckItem> Run(LineProject p, bool fix = true)
    {
        var r = new List<EquipmentCheckItem>();
        double start = p.Sections.Count > 0 ? p.Sections.Min(s => s.StartMm) : 0;
        double minLen = p.PackageTypes.Count > 0 ? p.PackageTypes.Min(t => t.LengthMm) : 0;

        // 1. La fotocellula di ingresso c'è
        if (!p.Photocells.Any(x => x.IsEntryPhotocell))
        {
            if (fix) { ProjectNormalizer.EnsureEntryPhotocell(p); }
            r.Add(new("ENTRY_MISSING", "Mancava la fotocellula di ingresso: " + (fix ? "aggiunta" : "da aggiungere") + $" a {ProjectNormalizer.DefaultEntryOffsetMm:0} mm dall'inizio linea.", fix, true));
        }
        var entry = p.Photocells.Where(x => x.IsEntryPhotocell).OrderBy(x => x.PositionMm).FirstOrDefault();
        if (entry != null)
        {
            // 2. Logica di presenza e abilitazione
            if (entry.DetectionMode != PhotocellDetectionMode.Presence)
            {
                if (fix) entry.DetectionMode = PhotocellDetectionMode.Presence;
                r.Add(new("ENTRY_MODE", "La fotocellula di ingresso deve usare la logica di presenza.", fix, true));
            }
            if (!entry.IsEnabled)
            {
                if (fix) entry.IsEnabled = true;
                r.Add(new("ENTRY_ENABLED", "La fotocellula di ingresso era disattivata.", fix, true));
            }
            // 3. Posizione: dentro la lunghezza del pacco più corto dall'inizio linea e a monte di tutte le altre
            double others = p.Photocells.Where(x => !ReferenceEquals(x, entry)).Select(x => x.PositionMm).DefaultIfEmpty(double.MaxValue).Min();
            bool outside = Math.Abs(entry.PositionMm - start) > 0.5;
            bool notFirst = entry.PositionMm >= others;
            if (outside || notFirst)
            {
                double target = start + ProjectNormalizer.DefaultEntryOffsetMm;
                if (others < double.MaxValue) target = Math.Min(target, others - 100);
                target = Math.Max(start, target);
                if (fix) entry.PositionMm = target;
                r.Add(new("ENTRY_POS", (notFirst ? "La fotocellula di ingresso non era la prima della linea" : "La fotocellula di ingresso era fuori posizione")
                    + $": {(fix ? "portata" : "da portare")} a {target:0} mm.", fix, true));
            }
            // 4. Nome corretto
            if (!(entry.Name.StartsWith("FC") && entry.Name.Contains("ingresso")))
            {
                if (fix) entry.Name = EntryName;
                r.Add(new("ENTRY_NAME", $"Prima fotocellula {(fix ? "rinominata" : "da rinominare")} in \"{EntryName}\".", fix, true));
            }
        }
        // 5. Fermo finale = fine dell'ultimo modulo
        if (p.Sections.Count > 0)
        {
            double expect = p.Sections.Max(x => x.EndMm);
            if (Math.Abs(p.Stopper.PositionMm - expect) > 0.5)
            {
                if (fix) ProjectNormalizer.AlignStopperToLastRoller(p);
                r.Add(new("STOPPER", $"Il fermo finale deve stare alla fine dell'ultimo modulo: {(fix ? "portato" : "da portare")} a {expect:0} mm.", fix, true));
            }
        }
        // 6. Vie a rulli senza vuoti o sovrapposizioni
        foreach (var (a, b, gap) in LineReflow.Discontinuities(p))
            r.Add(new("SECTION_GAP", gap > 0
                ? $"Tra {a.Name} e {b.Name} c'è un vuoto di {gap:0} mm."
                : $"{a.Name} e {b.Name} si sovrappongono di {-gap:0} mm.", false, true));
        // 7. Dispositivi dentro la linea
        double end = p.Sections.Count > 0 ? p.Sections.Max(s => s.EndMm) : 0;
        foreach (var s in p.Strappers)
            if (s.PositionMm < start || s.PositionMm > end) r.Add(new("STRAPPER_POS", $"{s.Name} è fuori dalla linea.", false, true));
        if (p.Transfer.PositionMm < start || p.Transfer.PositionMm > end) r.Add(new("TRANSFER_POS", "Il trasferitore parte fuori dalla linea.", false, true));

        // 8. Regole di layout
        if (StrapperTrack.MinStrapperPositionMm(p) is var minPos && p.Strappers.Any(x => x.PositionMm < minPos - 1e-6))
        {
            if (fix) StrapperTrack.ClampStrappersToTrackStart(p);
            r.Add(new("STRAPPER_START", $"Una legatrice era troppo vicina all'inizio della via rulli (servono almeno 1 m dall'inizio e lo spazio per encoder e fotocellula di riferimento): {(fix ? "portata" : "da portare")} a {minPos:0} mm.", fix, true));
        }
        if (LayoutRules.StrappersTooCloseToTransfer(p).Any())
        {
            if (fix) LayoutRules.ClampStrappers(p);
            r.Add(new("STRAPPER_TRF", $"Una legatrice era a meno di 3 m dal trasferitore: {(fix ? "portata" : "da portare")} a {LayoutRules.MaxStrapperPositionMm(p):0} mm.", fix, true));
        }
        if (!LayoutRules.TransferModuleMatches(p))
        {
            if (fix) LayoutRules.AlignTransferModule(p);
            r.Add(new("TRF_MODULE", $"L'ultimo modulo deve avere la stessa lunghezza e lo stesso inizio del trasferitore ({p.Transfer.LengthMm:0} mm): {(fix ? "allineato" : "da allineare")}.", fix, true));
        }
        if (LayoutRules.ExitPhotocellsOutOfPlace(p).Any())
        {
            if (fix) LayoutRules.AlignStrappingExit(p);
            r.Add(new("EXIT_PC", $"La fotocellula di uscita legatura deve stare mezzo passo dopo l'ultimo rullo della via di legatura: {(fix ? "spostata" : "da spostare")}.", fix, true));
        }
        if (LayoutRules.StopsOutOfPlace(p).Any())
        {
            if (fix) LayoutRules.SnapIntermediateStops(p);
            r.Add(new("STOP_POS", $"Il fermo scomparente senza spunta \"Spostamento manuale\" deve stare al centro o all'estremità del trasferitore, come scelto: {(fix ? "spostato" : "da spostare")}.", fix, true));
        }
        // Regola unica dell'encoder: primo rullo a monte della prima legatrice, almeno 1 m prima di essa; riferimento 300 mm prima, mai a monte di FC0 + 100 mm
        var encProblems = EncoderRule.Problems(p);
        if (encProblems.Count > 0)
        {
            if (fix) EncoderRule.Apply(p);
            bool tooClose = EncoderRule.StrapperTooCloseToStart(p);
            r.Add(new("ENCODER_RULE", $"Encoder e fotocellula di riferimento: {string.Join("; ", encProblems)}{(fix && !tooClose ? " (spostati)" : "")}.", fix && !tooClose, true));
        }
        if (StrappingAreaRules.ExitToTransferMm(p) < StrappingAreaRules.MinExitToTransferMm - 0.5)
        {
            var fixes = fix ? StrappingAreaRules.KeepExitAwayFromTransfer(p) : new List<string>();
            r.Add(new("EXIT_TRF_DIST", $"La fotocellula di uscita legatura (fine via di legatura) deve stare ad almeno 1 m dall'inizio del trasferitore: {(fix ? string.Join("; ", fixes) : "da allungare lo svincolo (o inserirne uno)")}.", fix, true));
        }
        if (PhotocellRules.MergeCandidate(p) != null)
        {
            var merged = fix ? PhotocellRules.MergeExitWithTransferStop(p) : new List<string>();
            r.Add(new("PC_MERGE", $"Fotocellula di uscita legatura e fotocellula di arresto davanti al trasferitore vicine (entro 1 m): {(fix ? string.Join("; ", merged) : "da unire in una sola")}.", fix, true));
        }
        if (PhotocellRules.TooCloseToTarget(p).Any())
        {
            var moved = fix ? PhotocellRules.KeepDistanceFromTarget(p) : PhotocellRules.TooCloseToTarget(p).Select(x => $"{x.Pc.Id} a {x.DistanceMm:0} mm").ToList();
            r.Add(new("PC_TARGET_DIST", $"Le fotocellule che rallentano o fermano devono stare ad almeno 2 m dal punto di arresto (di più se la velocità massima lo richiede): {string.Join("; ", moved)}.", fix, true));
        }
        if (StrappingAreaRules.SameSideConflicts(p).Any())
        {
            if (fix) StrappingAreaRules.SpreadStrappers(p);
            r.Add(new("STR_SIDE", $"Legatrici o reggettatrici sullo stesso lato a meno di 2 m: {(fix ? "distanziate" : "da distanziare")}.", fix, true));
        }
        if (r.Count == 0) r.Add(new("OK", "Equipaggiamenti a posto: nessuna anomalia.", false, false));
        return r;
    }
}
