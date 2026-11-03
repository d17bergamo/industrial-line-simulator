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
    public const string EntryName = "FC0 ingresso (identificazione)";

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
            bool outside = entry.PositionMm < start || (minLen > 0 && entry.PositionMm - start > minLen);
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
            if (entry.Name != EntryName)
            {
                if (fix) entry.Name = EntryName;
                r.Add(new("ENTRY_NAME", $"Prima fotocellula {(fix ? "rinominata" : "da rinominare")} in \"{EntryName}\".", fix, true));
            }
        }
        // 5. Fermo finale dopo l'ultimo rullo
        var last = ProjectNormalizer.LastRollerPositionMm(p);
        if (last != null)
        {
            double expect = last.Value + Math.Max(0, p.Stopper.DistanceFromLastRollerMm);
            if (Math.Abs(p.Stopper.PositionMm - expect) > 0.5)
            {
                if (fix) ProjectNormalizer.AlignStopperToLastRoller(p);
                r.Add(new("STOPPER", $"Il fermo finale non era dopo l'ultimo rullo: {(fix ? "portato" : "da portare")} a {expect:0} mm.", fix, true));
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

        if (r.Count == 0) r.Add(new("OK", "Equipaggiamenti a posto: nessuna anomalia.", false, false));
        return r;
    }
}
