using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Regole dell'area di legatura (sessione B):
/// - legatrici/reggettatrici sullo stesso lato ad almeno 2 m l'una dall'altra; se una si avvicina troppo le altre vengono
///   spinte (spostamento dinamico); macchine su lati opposti nessun vincolo;
/// - via rulli di svincolo tra la via di legatura e la via del trasferitore: il software decide se va sotto lo stesso
///   inverter della via di legatura (quando durante la legatura la testa del pacco deve entrare nello svincolo) o se resta
///   un inverter a sé;
/// - fermo scomparente e bilici: al centro o all'estremità del trasferitore, spostamento manuale solo con la spunta.
/// </summary>
public static class StrappingAreaRules
{
    /// <summary>Distanza minima tra due macchine di legatura sullo stesso lato della linea.</summary>
    public const double MinSameSideDistanceMm = 2000;

    /// <summary>Rulli dello svincolo nei modelli.</summary>
    public const int ReleaseModuleRollers = 4;

    // ------------------------------------------------------------------ Macchine sullo stesso lato

    /// <summary>Coppie di macchine abilitate sullo stesso lato a meno di 2 m.</summary>
    public static IEnumerable<(StrapperConfig A, StrapperConfig B)> SameSideConflicts(LineProject p)
    {
        foreach (var side in p.Strappers.GroupBy(s => s.Side))
        {
            var list = side.OrderBy(s => s.PositionMm).ToList();
            for (int i = 1; i < list.Count; i++)
                if (list[i].PositionMm - list[i - 1].PositionMm < MinSameSideDistanceMm - 0.5)
                    yield return (list[i - 1], list[i]);
        }
    }

    /// <summary>
    /// Mantiene almeno 2 m tra le macchine sullo stesso lato. Se <paramref name="moved"/> è indicata resta dove l'utente
    /// l'ha messa e le altre vengono spinte (a valle in avanti, a monte indietro); poi tutto resta tra 1 m dall'inizio
    /// della via e 3 m dal trasferitore (se serve anche la macchina spostata arretra o avanza). Restituisce le correzioni.
    /// </summary>
    public static List<string> SpreadStrappers(LineProject p, StrapperConfig? moved = null)
    {
        var done = new List<string>();
        double min = StrapperTrack.MinStrapperPositionMm(p);
        double max = LayoutRules.MaxStrapperPositionMm(p);
        foreach (var side in p.Strappers.GroupBy(s => s.Side))
        {
            var list = side.OrderBy(s => s.PositionMm).ThenBy(s => s == moved ? 0 : 1).ToList();
            if (list.Count < 2) continue;
            var before = list.ToDictionary(s => s, s => s.PositionMm);
            var pos = list.Select(s => s.PositionMm).ToArray();
            int k = moved != null ? list.IndexOf(moved) : -1;
            const double d = MinSameSideDistanceMm;
            if (k < 0)
            {
                for (int i = 1; i < pos.Length; i++) pos[i] = Math.Max(pos[i], pos[i - 1] + d);
            }
            else
            {
                for (int i = k + 1; i < pos.Length; i++) pos[i] = Math.Max(pos[i], pos[i - 1] + d);
                for (int i = k - 1; i >= 0; i--) pos[i] = Math.Min(pos[i], pos[i + 1] - d);
            }
            // Limiti della via: se la catena esce, si sposta tutta (anche la macchina mossa) restando a 2 m
            if (max > min && (pos.Length - 1) * d <= max - min + 1e-6)
            {
                if (pos[^1] > max) { pos[^1] = max; for (int i = pos.Length - 2; i >= 0; i--) pos[i] = Math.Min(pos[i], pos[i + 1] - d); }
                if (pos[0] < min) { pos[0] = min; for (int i = 1; i < pos.Length; i++) pos[i] = Math.Max(pos[i], pos[i - 1] + d); }
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (Math.Abs(pos[i] - before[list[i]]) < 0.5) continue;
                list[i].PositionMm = Math.Round(pos[i]);
                done.Add($"{list[i].Id}: {before[list[i]]:0} → {list[i].PositionMm:0} mm (almeno 2 m dalle macchine sullo stesso lato)");
            }
        }
        return done;
    }

    // ------------------------------------------------------------------ Via di legatura e svincolo

    /// <summary>Via a rulli di legatura: quella che contiene l'ultima legatrice abilitata (la prima via se non ce ne sono).</summary>
    public static RollerSection? StrappingModule(LineProject p) =>
        (StrapperTrack.LastStrapper(p) is { } ls ? StrapperTrack.SectionAt(p, ls.PositionMm) : null)
        ?? p.Sections.OrderBy(s => s.StartMm).FirstOrDefault();

    /// <summary>Vie di svincolo: tra la via di legatura e la via del trasferitore (ultima via).</summary>
    public static IReadOnlyList<RollerSection> ReleaseModules(LineProject p)
    {
        var strap = StrappingModule(p);
        var last = LayoutRules.TransferModule(p);
        if (strap == null || last == null || strap == last) return Array.Empty<RollerSection>();
        return p.Sections.Where(s => s.StartMm >= strap.EndMm - 1 && s.EndMm <= last.StartMm + 1 && s != last)
                         .OrderBy(s => s.StartMm).ToList();
    }

    /// <summary>Lunghezza minima della via di legatura dopo l'ultima legatrice: due rulli.</summary>
    public static double MinTrackAfterLastStrapperMm(RollerSection s) => 2 * s.RollerPitchMm;

    /// <summary>
    /// Posizione massima della testa del pacco durante la legatura: la legatura più lontana dalla testa (di solito quella di
    /// coda) si fa al più presto sotto la prima macchina, quindi la testa arriva almeno a prima macchina + distanza di quella
    /// legatura dalla testa. Si considerano <b>tutte</b> le tipologie di pacco della simulazione.
    /// </summary>
    public static double RequiredStrappingHeadMaxMm(LineProject p)
    {
        var first = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).FirstOrDefault();
        if (first == null) return double.NegativeInfinity;
        double rear = p.PackageTypes.Where(t => t.StrapPositionsMm.Count > 0).Select(t => t.StrapPositionsMm.Max()).DefaultIfEmpty(0).Max();
        return first.PositionMm + rear;
    }

    /// <summary>
    /// Svincolo: sempre con un inverter proprio. Se durante la legatura la testa del pacco deve entrarci (per una qualunque
    /// tipologia di pacco), lo svincolo "segue la legatura" (<see cref="RollerSection.FollowsStrapping"/>): si muove insieme
    /// alla via di legatura finché il pacco in legatura lo occupa, altrimenti porta via il pacco già legato.
    /// Restituisce le modifiche.
    /// </summary>
    public static List<string> DecideReleaseInverters(LineProject p)
    {
        var done = new List<string>();
        if (!p.Simulation.AutoReleaseInverters) return done;
        var strap = StrappingModule(p);
        if (strap == null || !p.Strappers.Any(s => s.IsEnabled)) return done;
        string zone = strap.EffectiveZoneId;
        double need = RequiredStrappingHeadMaxMm(p);
        double reach = strap.EndMm;
        foreach (var m in ReleaseModules(p))
        {
            bool follow = need > reach + 1e-6;
            if (m.EffectiveZoneId == zone && m.Id != zone)
            {
                m.CommandZoneId = string.Empty;
                done.Add($"{m.Id} (svincolo) con un inverter proprio");
            }
            if (follow != m.FollowsStrapping)
            {
                m.FollowsStrapping = follow;
                done.Add(follow
                    ? $"{m.Id} (svincolo) segue la via di legatura {strap.Id} durante la legatura: per le legature di coda la testa del pacco arriva a {need:0} mm, oltre la fine della via di legatura"
                    : $"{m.Id} (svincolo) indipendente: la legatura si fa tutta sulla via di legatura {strap.Id}");
            }
            if (follow) reach = m.EndMm;
        }
        return done;
    }

    // ------------------------------------------------------------------ Fermo scomparente e bilici sul trasferitore

    /// <summary>Posizione del fermo scomparente: centro del trasferitore oppure estremità a monte (opposta al fermo fisso).</summary>
    public static double StopPositionMm(LineProject p, DevicePlacement placement) =>
        placement == DevicePlacement.End ? p.Transfer.PositionMm : p.Transfer.PositionMm + p.Transfer.LengthMm / 2;

    /// <summary>Fermi scomparenti non manuali fuori dalla posizione scelta.</summary>
    public static IEnumerable<IntermediateStopConfig> StopsOutOfPlace(LineProject p) =>
        p.IntermediateStops.Where(f => !f.ManualPosition && Math.Abs(f.PositionMm - StopPositionMm(p, f.Placement)) >= 0.5);

    /// <summary>Porta i fermi scomparenti non manuali al centro o all'estremità scelta. Restituisce le correzioni.</summary>
    public static List<string> PlaceStops(LineProject p)
    {
        var done = new List<string>();
        foreach (var f in StopsOutOfPlace(p).ToList())
        {
            double x = StopPositionMm(p, f.Placement);
            done.Add($"{f.Id}: {f.PositionMm:0} → {x:0} mm ({(f.Placement == DevicePlacement.End ? "estremità" : "centro")} del trasferitore)");
            f.PositionMm = x;
        }
        return done;
    }

    /// <summary>
    /// Bilici non manuali: lunghezza = trasferitore / numero di bilici, uno accanto all'altro sul trasferitore. Quelli
    /// "all'estremità" prendono i posti alle estremità (prima a valle), quelli "al centro" i posti centrali.
    /// </summary>
    public static bool LayoutScales(LineProject p)
    {
        var auto = p.Scales.Where(s => !s.ManualPosition).OrderBy(s => s.StartMm).ToList();
        int n = auto.Count;
        if (n == 0 || p.Transfer.LengthMm <= 0) return false;
        double len = p.Transfer.LengthMm / n;
        var endSlots = new List<int>();
        for (int a = n - 1, b = 0; endSlots.Count < n; a--, b++)
        {
            if (!endSlots.Contains(a)) endSlots.Add(a);
            if (endSlots.Count < n && !endSlots.Contains(b)) endSlots.Add(b);
        }
        var centerSlots = Enumerable.Range(0, n).OrderBy(i => Math.Abs(i - (n - 1) / 2.0)).ThenByDescending(i => i).ToList();
        var free = new HashSet<int>(Enumerable.Range(0, n));
        var slot = new Dictionary<WeighingScaleConfig, int>();
        foreach (var s in auto.Where(s => s.Placement == DevicePlacement.End))
        {
            int i = endSlots.First(free.Contains); free.Remove(i); slot[s] = i;
        }
        foreach (var s in auto.Where(s => s.Placement == DevicePlacement.Center))
        {
            int i = centerSlots.First(free.Contains); free.Remove(i); slot[s] = i;
        }
        bool changed = false;
        foreach (var (s, i) in slot)
        {
            double start = p.Transfer.PositionMm + i * len;
            if (Math.Abs(s.StartMm - start) < 0.5 && Math.Abs(s.LengthMm - len) < 0.5) continue;
            s.StartMm = start;
            s.LengthMm = len;
            changed = true;
        }
        return changed;
    }

    // ------------------------------------------------------------------ Fotocellule di riferimento e di uscita legatura

    /// <summary>Distanza minima della fotocellula di riferimento (prima della prima legatrice) dopo FC0.</summary>
    public const double MinReferenceAfterEntryMm = 100;

    /// <summary>Distanza minima tra la fotocellula di uscita legatura (fine via di legatura) e l'inizio del trasferitore.</summary>
    public const double MinExitToTransferMm = 1000;

    /// <summary>Posizione minima ammessa per la fotocellula di riferimento: 100 mm dopo FC0 (o dopo l'inizio della linea).</summary>
    public static double MinReferencePhotocellMm(LineProject p)
    {
        var fc0 = p.Photocells.FirstOrDefault(x => x.IsEntryPhotocell);
        double start = fc0?.PositionMm ?? (p.Sections.Count > 0 ? p.Sections.Min(s => s.StartMm) : 0);
        return start + MinReferenceAfterEntryMm;
    }

    /// <summary>Fotocellule di riferimento più a monte del minimo (FC0 + 100 mm).</summary>
    public static IEnumerable<PhotocellConfig> ReferencePhotocellsBeforeEntry(LineProject p)
    {
        double min = MinReferencePhotocellMm(p);
        return p.Photocells.Where(x => x.Purpose == PhotocellPurpose.StrappingReference && x.PositionMm < min - 0.5);
    }

    /// <summary>
    /// Regola 1: la fotocellula di riferimento non va mai più a monte di FC0: se è prima di FC0 + 100 mm viene riportata lì e
    /// l'encoder resta 300 mm dopo di essa. Restituisce le correzioni.
    /// </summary>
    public static List<string> KeepReferenceAfterEntry(LineProject p)
    {
        var done = new List<string>();
        double min = MinReferencePhotocellMm(p);
        foreach (var pc in ReferencePhotocellsBeforeEntry(p).ToList())
        {
            done.Add($"{pc.Id}: {pc.PositionMm:0} → {min:0} mm (la fotocellula di riferimento non può stare a monte di FC0)");
            pc.PositionMm = min;
            double enc = min + ProjectNormalizer.ReferencePhotocellOffsetMm;
            if (p.StrappingEncoder.IsEnabled && p.StrappingEncoder.PositionMm < enc - 0.5)
            {
                done.Add($"{p.StrappingEncoder.Id}: encoder {p.StrappingEncoder.PositionMm:0} → {enc:0} mm (300 mm dopo la fotocellula di riferimento)");
                p.StrappingEncoder.PositionMm = enc;
            }
        }
        return done;
    }

    /// <summary>Distanza tra la fine della via di legatura (fotocellula di uscita legatura) e l'inizio del trasferitore.</summary>
    public static double ExitToTransferMm(LineProject p)
    {
        var strap = StrappingModule(p);
        var last = LayoutRules.TransferModule(p);
        if (strap == null || last == null || strap == last || p.Transfer.LengthMm <= 0 || p.Transfer.PositionMm < strap.StartMm)
            return double.PositiveInfinity;          // nessun trasferitore a valle della via di legatura
        return p.Transfer.PositionMm - strap.EndMm;
    }

    /// <summary>
    /// Regola 2: la fotocellula di uscita legatura resta alla fine della via di legatura e deve stare ad almeno 1 m dal
    /// trasferitore. Se è più vicina non si toccano fotocellula, legatrici e via di legatura: si allunga lo svincolo con rulli
    /// interi (o se ne inserisce uno se manca); trasferitore, bilici e fermi avanzano insieme. Restituisce le correzioni.
    /// </summary>
    public static List<string> KeepExitAwayFromTransfer(LineProject p)
    {
        var done = new List<string>();
        var strap = StrappingModule(p);
        var last = LayoutRules.TransferModule(p);
        if (strap == null || last == null || strap == last) return done;
        double deficit = MinExitToTransferMm - ExitToTransferMm(p);
        if (deficit <= 0.5) return done;
        var release = ReleaseModules(p).LastOrDefault();
        if (release != null)
        {
            double pitch = release.RollerPitchMm;
            int n = (int)Math.Ceiling(deficit / pitch - 1e-9);
            double oldEnd = release.EndMm;
            LineReflow.Fit(release, release.StartMm, oldEnd + n * pitch);
            LineReflow.AfterResize(p, release, oldEnd);
            done.Add($"Svincolo {release.Id} allungato di {n} {(n == 1 ? "rullo" : "rulli")}: la fotocellula di uscita legatura deve stare ad almeno 1 m dal trasferitore");
        }
        else
        {
            double pitch = strap.RollerPitchMm > 0 ? strap.RollerPitchMm : RollerSection.DefaultPitchMm;
            int n = Math.Max(2, (int)Math.Ceiling(deficit / pitch - 1e-9));     // una via ha almeno 2 rulli
            double at = strap.EndMm;
            var exits = p.Photocells.Where(x => x.Purpose == PhotocellPurpose.StrappingExit && Math.Abs(x.PositionMm - at) < 1).ToList();
            LineReflow.OpenGap(p, at, n * pitch);
            foreach (var x in exits) x.PositionMm = at;                         // la fotocellula resta alla fine della via di legatura
            int k = 1;
            while (p.Sections.Any(s => s.Id == "M" + k)) k++;
            var m = new RollerSection
            {
                Id = "M" + k, Name = "Modulo svincolo", RollerPitchMm = pitch, RollerDiameterMm = strap.RollerDiameterMm,
                WidthMm = strap.WidthMm, DefaultSpeed = strap.DefaultSpeed
            };
            LineReflow.Fit(m, at, at + n * pitch);
            p.Sections.Add(m);
            done.Add($"Inserito lo svincolo {m.Id} da {n} rulli: la fotocellula di uscita legatura deve stare ad almeno 1 m dal trasferitore");
        }
        ProjectNormalizer.AlignStopperToLastRoller(p);
        return done;
    }

    /// <summary>Applica le regole dell'area di legatura (chiamato da <see cref="LayoutRules.Enforce"/>).</summary>
    public static List<string> Enforce(LineProject p)
    {
        var done = new List<string>();
        done.AddRange(KeepReferenceAfterEntry(p));
        done.AddRange(KeepExitAwayFromTransfer(p));
        done.AddRange(SpreadStrappers(p));
        if (LayoutScales(p)) done.Add("Bilici sistemati sul trasferitore (lunghezza = trasferitore / numero di bilici)");
        done.AddRange(PlaceStops(p));
        done.AddRange(DecideReleaseInverters(p));
        return done;
    }
}
