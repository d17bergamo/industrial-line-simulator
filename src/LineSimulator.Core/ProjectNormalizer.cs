using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Adeguamenti automatici dei progetti (migrazioni e requisiti obbligatori).</summary>
public static class ProjectNormalizer
{
    /// <summary>Distanza della fotocellula di ingresso aggiunta automaticamente dall'inizio linea.</summary>
    public const double DefaultEntryOffsetMm = 0;

    /// <summary>
    /// Nomi delle fotocellule: "FC" + progressivo da monte a valle, con lo scopo tra parentesi ("FC2 (accumulo)").
    /// Gli Id restano invariati (sono usati da regole e comandi). True se qualche nome è cambiato.
    /// </summary>
    public static bool RenumberPhotocellNames(LineProject p)
    {
        bool changed = false;
        int n = 0;
        foreach (var pc in p.Photocells.OrderBy(x => x.PositionMm))
        {
            n++;
            var m = System.Text.RegularExpressions.Regex.Match((pc.Name ?? "").Trim(), @"^FC\w*\s*(?:\((.*)\))?\s*(.*)$");
            string purpose = (m.Success ? (m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value) : pc.Name ?? "").Trim();
            if (purpose.Length == 0 || purpose.Equals("Nuova fotocellula", StringComparison.OrdinalIgnoreCase))
                purpose = pc.Purpose switch
                {
                    PhotocellPurpose.TransferStop => "arresto davanti al trasferitore",
                    PhotocellPurpose.StrappingReference => "riferimento encoder di legatura",
                    PhotocellPurpose.StrappingExit => "uscita via rulli di legatura",
                    _ => pc.IsEntryPhotocell ? "ingresso (identificazione)" : "generica"
                };
            string name = $"FC{n} ({purpose})";
            if (pc.Name != name) { pc.Name = name; changed = true; }
        }
        return changed;
    }


    /// <summary>
    /// Garantisce la presenza della fotocellula di ingresso (obbligatoria). Se nessuna fotocellula è marcata
    /// come ingresso ne aggiunge una (solo segnale) a 300 mm dall'inizio linea. Restituisce true se ha modificato il progetto.
    /// </summary>
    public static bool EnsureEntryPhotocell(LineProject p)
    {
        if (p.Photocells.Any(pc => pc.IsEntryPhotocell)) return false;
        double start = p.Sections.Count > 0 ? p.Sections.Min(s => s.StartMm) : 0;
        var ids = p.Photocells.Select(x => x.Id).ToHashSet();
        string id = "FC0";
        for (int n = 0; ids.Contains(id); n++) id = "FCI" + n;
        p.Photocells.Insert(0, new PhotocellConfig
        {
            Id = id,
            Name = "FC1 (ingresso identificazione)",
            PositionMm = start + DefaultEntryOffsetMm,
            ResponseTimeMs = 20,
            IsEntryPhotocell = true,
            Action = PhotocellAction.None
        });
        return true;
    }

    /// <summary>
    /// Il fermo finale sta sempre dopo l'ultimo rullo dell'ultima via a rulli: la posizione della battuta si calcola
    /// dalla sola distanza rullo-fermo. Restituisce true se la posizione è cambiata.
    /// </summary>
    public static bool AlignStopperToLastRoller(LineProject p) => AlignStopperToLastRoller(p, null);

    /// <summary>
    /// Come sopra; <paramref name="stopperPhotocell"/> (l'ultima fotocellula prima del fermo, da <see cref="StopperPhotocell"/>
    /// letta prima dello spostamento) si sposta con il fermo e ne mantiene la distanza.
    /// </summary>
    public static bool AlignStopperToLastRoller(LineProject p, PhotocellConfig? stopperPhotocell)
    {
        if (p.Sections.Count == 0) return false;
        var lastSec = p.Sections.OrderBy(s => s.EndMm).Last();
        double pos = lastSec.EndMm;                       // il fermo sta alla fine dell'ultimo modulo (= fine del trasferitore)
        bool changed = Math.Abs(pos - p.Stopper.PositionMm) >= 1e-9 || Math.Abs(p.Stopper.DistanceFromLastRollerMm - lastSec.EndOverhangMm) >= 1e-9;
        double delta = pos - p.Stopper.PositionMm;
        p.Stopper.DistanceFromLastRollerMm = lastSec.EndOverhangMm;
        p.Stopper.PositionMm = pos;
        if (stopperPhotocell != null && Math.Abs(delta) > 1e-9) stopperPhotocell.PositionMm += delta;
        return changed;
    }

    /// <summary>
    /// Ultima fotocellula in fondo prima del fermo finale (nell'ultima via a rulli, esclusa l'ingresso): segue il fermo
    /// quando si sposta o si allunga l'area del trasferitore. Null se non c'è.
    /// </summary>
    public static PhotocellConfig? StopperPhotocell(LineProject p)
    {
        if (p.Sections.Count == 0) return null;
        var lastSec = p.Sections.OrderBy(s => s.EndMm).Last();
        return p.Photocells
            .Where(x => !x.IsEntryPhotocell && x.PositionMm >= lastSec.StartMm - 1e-6 && x.PositionMm <= p.Stopper.PositionMm + 1e-6)
            .OrderByDescending(x => x.PositionMm).FirstOrDefault();
    }

    /// <summary>
    /// Le fotocellule non escono mai dalle vie a rulli: riporta dentro (tra inizio della prima via e fine dell'ultima)
    /// quelle fuori. Restituisce true se ne ha spostata qualcuna.
    /// </summary>
    public static bool ClampPhotocellsToRollers(LineProject p)
    {
        if (p.Sections.Count == 0) return false;
        double min = p.Sections.Min(s => s.StartMm), max = p.Sections.Max(s => s.EndMm);
        bool changed = false;
        foreach (var x in p.Photocells)
        {
            double c = Math.Clamp(x.PositionMm, min, max);
            if (Math.Abs(c - x.PositionMm) < 1e-9) continue;
            x.PositionMm = c;
            changed = true;
        }
        return changed;
    }

    /// <summary>La prima fotocellula (ingresso) sta sempre sullo zero della via rulli. Restituisce true se è stata spostata.</summary>
    public static bool AlignEntryPhotocell(LineProject p)
    {
        if (p.Sections.Count == 0) return false;
        var entry = p.Photocells.Where(x => x.IsEntryPhotocell).OrderBy(x => x.PositionMm).FirstOrDefault();
        double start = p.Sections.Min(s => s.StartMm);
        if (entry == null || Math.Abs(entry.PositionMm - start) < 1e-9) return false;
        entry.PositionMm = start;
        return true;
    }

    /// <summary>Distanza di default dei dispositivi KNOF / compattatore a monte della macchina di legatura.</summary>
    public const double DefaultClampOffsetMm = 450;
    /// <summary>Distanza della fotocellula di riferimento a monte dell'encoder di legatura.</summary>
    public const double ReferencePhotocellOffsetMm = 300;

    /// <summary>
    /// Distanza della fotocellula di arresto dal trasferitore: stessa regola di tutte le fotocellule che fermano
    /// (<see cref="PhotocellRules.RequiredDistanceMm(LineProject, PhotocellConfig, LineTopology?)"/>): almeno 2 m, di più se
    /// lo spazio per fermarsi dalla velocità massima del modulo a monte del trasferitore lo richiede.
    /// </summary>
    public static double TransferStopDistanceMm(LineProject p)
    {
        // regola dell'utente (2026-11-10): Vmax²/(2a) + 0,2 s × Vmax dall'ingombro più a monte dei pacchi sul trasferitore
        if (p.Sections.Count == 0) return PhotocellRules.MinDistanceToTargetMm;
        return p.Transfer.PositionMm - TransferEntryRule.EntryPhotocellPositionMm(p);
    }

    /// <summary>Prima macchina di legatura abilitata (KNCA o SMA) nella stazione, a monte.</summary>
    public static StrapperConfig? FirstStrappingMachine(LineProject p) =>
        p.StrappingStation.IsEnabled ? p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).FirstOrDefault() : null;

    /// <summary>
    /// Sensori di processo obbligatori, aggiunti in automatico se mancano (e posizioni di default dei dispositivi):
    /// <list type="bullet">
    /// <item>fotocellula di arresto davanti al primo trasferitore sollevabile (almeno 2 m o lo spazio di frenata dalla velocità massima);</item>
    /// <item>encoder incrementale sul primo rullo a monte della prima macchina di legatura, preceduto da una fotocellula di riferimento;</item>
    /// <item>KNOF e compattatore senza posizione: 450 mm a monte della prima macchina di legatura.</item>
    /// </list>
    /// Restituisce la descrizione di quanto aggiunto (vuota se il progetto era già completo).
    /// </summary>
    public static List<string> EnsureProcessSensors(LineProject p)
    {
        var added = new List<string>();
        if (!p.Simulation.AutoAddProcessSensors || p.Sections.Count == 0) return added;
        var topo = new LineTopology(p);
        var ids = new HashSet<string>(p.Photocells.Select(x => x.Id));
        string NewId(string prefix)
        {
            int n = 1;
            while (ids.Contains(prefix + n)) n++;
            ids.Add(prefix + n);
            return prefix + n;
        }

        // Fotocellula di arresto davanti al trasferitore sollevabile
        if (!p.Photocells.Any(x => x.ActsAsTransferStop) && p.Transfer.LengthMm > 0)
        {
            double x = Math.Round(p.Transfer.PositionMm - TransferStopDistanceMm(p));
            var zone = topo.ZoneAt(Units.MmToM(x));
            if (zone != null)
            {
                var pc = new PhotocellConfig
                {
                    Id = NewId("FCT"), Name = "Arresto davanti al trasferitore", PositionMm = x, ResponseTimeMs = 20,
                    Purpose = PhotocellPurpose.TransferStop, IsAutoGenerated = true, ControlledInverterId = zone.Id,
                    Action = PhotocellAction.None
                };
                p.Photocells.Add(pc);
                added.Add($"{pc.Id}: fotocellula di arresto davanti al trasferitore a {x:0} mm (comanda {zone.Id})");
            }
        }

        // Encoder e fotocellula di riferimento prima della prima macchina di legatura
        var first = FirstStrappingMachine(p);
        if (first != null && !p.Photocells.Any(x => x.Purpose == PhotocellPurpose.StrappingReference))
        {
            var station = topo.Zone(p.StrappingStation.InverterId);
            double enc = EncoderRule.TargetMm(p) ?? Math.Round(first.PositionMm - EncoderRule.MinToStrapperMm);   // regola unica dell'encoder
            if (!p.StrappingEncoder.IsEnabled)
            {
                p.StrappingEncoder.IsEnabled = true;
                p.StrappingEncoder.PositionMm = enc;
                p.StrappingEncoder.IsAutoGenerated = true;
                added.Add($"{p.StrappingEncoder.Id}: encoder incrementale a {enc:0} mm");
            }
            double x = Math.Round(p.StrappingEncoder.PositionMm - ReferencePhotocellOffsetMm);
            if (topo.ZoneAt(Units.MmToM(x)) != null)
            {
                var pc = new PhotocellConfig
                {
                    Id = NewId("FCE"), Name = "Riferimento encoder di legatura", PositionMm = x, ResponseTimeMs = 20,
                    Purpose = PhotocellPurpose.StrappingReference, IsAutoGenerated = true,
                    ControlledInverterId = station?.Id ?? string.Empty, Action = PhotocellAction.None
                };
                p.Photocells.Add(pc);
                added.Add($"{pc.Id}: fotocellula di riferimento encoder a {x:0} mm");
            }
        }

        // Posizione di default di stringifascio e compattatore
        if (first != null)
        {
            if (p.BundleClamp.IsEnabled && p.BundleClamp.PositionMm <= 0)
            {
                p.BundleClamp.PositionMm = first.PositionMm - DefaultClampOffsetMm;
                added.Add($"{p.BundleClamp.Id}: posizione di default {p.BundleClamp.PositionMm:0} mm (450 mm a monte di {first.Id})");
            }
            if (p.Compactor.IsEnabled && p.Compactor.PositionMm <= 0)
            {
                p.Compactor.PositionMm = first.PositionMm - DefaultClampOffsetMm;
                added.Add($"{p.Compactor.Id}: posizione di default {p.Compactor.PositionMm:0} mm (450 mm a monte di {first.Id})");
            }
        }
        return added;
    }
}
