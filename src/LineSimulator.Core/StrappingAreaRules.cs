using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Regole dell'area di legatura (sessione B):
/// - legatrici/reggettatrici sullo stesso lato ad almeno 2 m l'una dall'altra; se una si avvicina troppo le altre vengono
///   spinte (spostamento dinamico); su lati opposti almeno 500 mm, frontali solo con una delle due disabilitata (arretrata);
/// - via rulli di svincolo tra la via di legatura e la via del trasferitore: il software decide se va sotto lo stesso
///   inverter della via di legatura (quando durante la legatura la testa del pacco deve entrare nello svincolo) o se resta
///   un inverter a sé;
/// - fermo scomparente e bilici: al centro o all'estremità del trasferitore, spostamento manuale solo con la spunta.
/// </summary>
public static class StrappingAreaRules
{
    /// <summary>Distanza minima tra due macchine di legatura sullo stesso lato della linea.</summary>
    public const double MinSameSideDistanceMm = 2000;

    /// <summary>
    /// Macchine su lati opposti (regola dell'utente del 2026-11-13): se non sono frontali, almeno 500 mm tra i piani di legatura;
    /// frontali (stessa posizione) solo se una delle due è disabilitata, cioè fisicamente arretrata.
    /// </summary>
    public const double MinOppositeSideDistanceMm = 500;

    /// <summary>Tolleranza per considerare due macchine frontali (stessa posizione lungo la linea).</summary>
    public const double FacingToleranceMm = 0.5;

    /// <summary>Coppie di macchine abilitate su lati opposti a meno di 500 mm ma non frontali (si correggono distanziandole).</summary>
    public static IEnumerable<(StrapperConfig A, StrapperConfig B)> OppositeSideConflicts(LineProject p) =>
        OppositePairs(p).Where(x => Math.Abs(x.B.PositionMm - x.A.PositionMm) > FacingToleranceMm
                                    && Math.Abs(x.B.PositionMm - x.A.PositionMm) < MinOppositeSideDistanceMm - 0.5);

    /// <summary>Coppie di macchine frontali entrambe abilitate: una delle due va disabilitata (arretrata).</summary>
    public static IEnumerable<(StrapperConfig A, StrapperConfig B)> FacingBothEnabled(LineProject p) =>
        OppositePairs(p).Where(x => Math.Abs(x.B.PositionMm - x.A.PositionMm) <= FacingToleranceMm);

    private static IEnumerable<(StrapperConfig A, StrapperConfig B)> OppositePairs(LineProject p)
    {
        var en = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).ToList();
        for (int i = 0; i < en.Count; i++)
            for (int j = i + 1; j < en.Count && en[j].PositionMm - en[i].PositionMm < MinOppositeSideDistanceMm; j++)
                if (en[i].Side != en[j].Side) yield return (en[i], en[j]);
    }

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
        for (int round = 0; round < 4; round++)
        {
            int n = done.Count;
            done.AddRange(SpreadSameSide(p, moved));
            done.AddRange(SpreadOppositeSide(p, moved));
            if (done.Count == n) break;
        }
        return done;
    }

    /// <summary>
    /// Macchine abilitate su lati opposti a meno di 500 mm (non frontali): si allontana quella non spostata dall'utente
    /// (di norma quella a valle avanza). Le frontali restano: una delle due va disabilitata dall'utente.
    /// </summary>
    private static List<string> SpreadOppositeSide(LineProject p, StrapperConfig? moved)
    {
        var done = new List<string>();
        foreach (var (a, b) in OppositeSideConflicts(p).ToList())
        {
            const double d = MinOppositeSideDistanceMm;
            var shift = b == moved ? a : b;
            double before = shift.PositionMm;
            shift.PositionMm = shift == b ? Math.Round(a.PositionMm + d) : Math.Round(b.PositionMm - d);
            done.Add($"{shift.Id}: {before:0} → {shift.PositionMm:0} mm (almeno 500 mm dalla macchina sul lato opposto)");
        }
        return done;
    }

    private static List<string> SpreadSameSide(LineProject p, StrapperConfig? moved)
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
    /// legatura dalla testa. Si considerano tutte le tipologie di pacco della simulazione: la configurazione di inverter
    /// e fotocellule deve andare bene per tutti i pacchi.
    /// </summary>
    public static double RequiredStrappingHeadMaxMm(LineProject p)
    {
        var first = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).FirstOrDefault();
        if (first == null) return double.NegativeInfinity;
        var types = p.PackageTypes;
        double rear = types.Where(t => t.StrapPositionsMm.Count > 0).Select(t => t.StrapPositionsMm.Max()).DefaultIfEmpty(0).Max();
        return first.PositionMm + rear;
    }

    /// <summary>
    /// Decide gli inverter di legatura e svincolo: lo svincolo va sotto lo stesso inverter della via di legatura solo se,
    /// durante la legatura, la testa del pacco deve entrarci (via di legatura troppo corta per i pacchi lunghi); altrimenti
    /// resta un inverter a sé e può portare via il pacco già legato mentre il successivo si lega. Restituisce le modifiche.
    /// </summary>
    public static List<string> DecideReleaseInverters(LineProject p)
    {
        var done = new List<string>();
        var strap = StrappingModule(p);
        if (strap == null || !p.Strappers.Any(s => s.IsEnabled)) return done;
        string zone = strap.EffectiveZoneId;
        double need = RequiredStrappingHeadMaxMm(p);
        double reach = strap.EndMm;
        if (!p.Simulation.AutoReleaseInverters) return done;
        var trf = LayoutRules.TransferModule(p);
        // Regola dell'utente (2026-11-13 23:20): di default lo svincolo ha l'inverter dedicato; se la legatura lo impegna
        // (legature di coda dei pacchi lunghi) i suoi motori vengono commutati sull'inverter di legatura solo in quel momento.
        // Se fisicamente ci sta il pacco più corto fa anche da parcheggio. Unito alla legatura solo se scelto
        // (JoinStrappingInverter, proposto dall'ottimizzatore quando la linea supera l'obiettivo di oltre il 25%).
        foreach (var m in ReleaseModules(p))
        {
            bool engaged = need > reach + 1e-6;
            bool joined = m.EffectiveZoneId == zone;
            if (m.JoinStrappingInverter)
            {
                if (m.FollowsStrapping) { m.FollowsStrapping = false; done.Add($"{m.Id} (svincolo) unito all'inverter di legatura: motori commutabili non servono"); }
                if (!joined) { m.CommandZoneId = zone; done.Add($"{m.Id} (svincolo) unito all'inverter di legatura {strap.Id} (scelta: un inverter in meno)"); }
                if (engaged) reach = m.EndMm;
                continue;
            }
            if (m.EffectiveZoneId != m.Id)
            {
                m.CommandZoneId = string.Empty;
                done.Add($"{m.Id} (svincolo) con inverter dedicato" + (CanPark(p, m) ? $": fa anche da parcheggio, ci sta il pacco più corto ({ShortestPackageMm(p):0} mm) con i margini" : ""));
            }
            if (engaged && !m.FollowsStrapping)
            {
                m.FollowsStrapping = true;
                done.Add($"{m.Id} (svincolo): motori commutabili sull'inverter di legatura {strap.Id} durante le legature di coda (la testa del pacco più lungo arriva a {need:0} mm, oltre la fine della via di legatura)");
            }
            else if (!engaged && m.FollowsStrapping)
            {
                m.FollowsStrapping = false;
                done.Add($"{m.Id} (svincolo): motori commutabili non necessari, la legatura si fa tutta sulla via di legatura {strap.Id}");
            }
            if (engaged) reach = m.EndMm;
        }
        // la via davanti al trasferitore (fronte accumulo) ha sempre il suo inverter
        if (trf != null && trf.EffectiveZoneId != trf.Id)
        {
            trf.CommandZoneId = string.Empty;
            done.Add($"{trf.Id} (fronte accumulo) con inverter proprio");
        }
        // Regola dell'utente (2026-11-21, B10): durante la legatura di coda il pacco può andare oltre lo svincolo, sulla via del
        // trasferitore. In quel caso i suoi rulli prendono i comandi della legatura (come lo svincolo) per tutta la fermata;
        // la fermata parte solo con il trasferitore a riposo e la via libera (consenso nel motore).
        if (trf != null && !ReferenceEquals(trf, strap))
        {
            bool onTransfer = need > reach + 1e-6 && need > trf.StartMm + 1e-6;
            if (onTransfer && !trf.FollowsStrapping)
            {
                trf.FollowsStrapping = true;
                done.Add($"{trf.Id} (via del trasferitore): motori commutabili sull'inverter di legatura {strap.Id} durante le legature di coda (la testa del pacco più lungo arriva a {need:0} mm, oltre lo svincolo); la fermata parte solo con trasferitore a riposo e via libera");
            }
            else if (!onTransfer && trf.FollowsStrapping)
            {
                trf.FollowsStrapping = false;
                done.Add($"{trf.Id} (via del trasferitore): motori commutabili non necessari, la legatura di coda non supera lo svincolo");
            }
        }
        return done;
    }

    // ------------------------------------------------------------------ Svincolo lungo: sezioni-parcheggio

    /// <summary>
    /// Regola dell'utente (2026-11-20): se lo svincolo è lungo, il programma lo divide da solo in sezioni di inverter calcolate
    /// sul pacco di riferimento da 12 m (B12), il massimo numero possibile (minimo 1), per avere più parcheggi: un parcheggio è una sezione di
    /// inverter. Le sezioni sono fatte di rulli interi, il più possibile uguali tra loro (differenza di un rullo); ognuna ha il
    /// suo inverter dedicato e la sua fotocellula (di accumulo, a 2 m dalla fine). Agisce solo se lo svincolo è una via sola:
    /// se l'utente ha già diviso lo svincolo a mano non lo tocca. Restituisce le modifiche.
    /// </summary>
    public static List<string> SplitReleaseIntoSections(LineProject p)
    {
        var done = new List<string>();
        if (!p.Simulation.AutoReleaseInverters || !p.Strappers.Any(s => s.IsEnabled)) return done;
        var mods = ReleaseModules(p);
        if (mods.Count != 1) return done;
        var m = mods[0];
        if (m.JoinStrappingInverter || m.RollerPitchMm <= 0) return done;
        double longest = p.PackageTypes.Count == 0 ? 0 : p.PackageTypes.Max(t => t.LengthMm);
        if (longest <= 0) return done;
        double pitch = m.RollerPitchMm;
        int rollers = (int)Math.Round(m.LengthMm / pitch);
        if (rollers < 2 || Math.Abs(rollers * pitch - m.LengthMm) > 1) return done;          // via non a rulli interi: non si divide
        // Regola dell'utente (2026-11-21, B12): le sezioni si calcolano su un pacco di riferimento da 12 m (mai più corte), per avere
        // più parcheggi; un pacco più lungo si parcheggia su più sezioni consecutive, purché lo svincolo lo contenga con il margine
        if (rollers * pitch < longest + ParkingMarginMm - 1e-6) return done;
        int rMin = (int)Math.Ceiling((ParkingReferenceMm + ParkingMarginMm) / pitch - 1e-9);   // rulli minimi di una sezione
        int n = rMin > 0 ? rollers / rMin : 1;
        if (n < 2) return done;

        int baseRollers = rollers / n, extra = rollers % n;                                  // il resto, un rullo per sezione, alle prime (a monte)
        var sections = new List<RollerSection>();
        double start = m.StartMm;
        for (int j = 0; j < n; j++)
        {
            int r = baseRollers + (j < extra ? 1 : 0);
            var s = j == 0 ? m : CloneSection(m);
            if (j > 0)
            {
                s.Id = $"{m.Id}.{j + 1}";
                s.Name = $"{m.Name} - sezione {j + 1}";
                s.CommandZoneId = string.Empty;
                s.FollowsStrapping = false;
                s.JoinStrappingInverter = false;
                s.SplitAtMm = 0;
                p.Sections.Add(s);
            }
            s.StartMm = start;
            s.RollerCount = r;
            s.LengthMm = r * pitch;
            start += s.LengthMm;
            sections.Add(s);
        }
        // una fotocellula di accumulo a 2 m dalla fine di ogni sezione tranne l'ultima (che ha quella di arresto davanti al trasferitore)
        for (int j = 0; j < n - 1; j++)
        {
            p.Photocells.Add(new PhotocellConfig
            {
                Id = $"FCS{m.Id}.{j + 1}",
                Name = $"Fotocellula parcheggio {m.Id} sezione {j + 1}",
                PositionMm = sections[j].EndMm - PhotocellRules.MinDistanceToTargetMm,
                ControlledInverterId = sections[j].Id,
                Action = PhotocellAction.Accumulation,
                IsAutoGenerated = true,
                ResponseTimeMs = 20
            });
        }
        string rl = string.Join(", ", sections.Select(s => s.RollerCount));
        done.Add($"{m.Id} (svincolo, {rollers} rulli = {rollers * pitch / 1000:0.##} m): diviso in {n} sezioni di inverter ({rl} rulli), una per parcheggio: " +
                 $"sezioni calcolate su un pacco di {ParkingReferenceMm:0} mm con frenata e margine ({ParkingMarginMm:0} mm), minimo {rMin} rulli; il pacco più lungo ({longest:0} mm) si parcheggia su più sezioni consecutive; ogni sezione ha il suo inverter e la sua fotocellula");
        return done;
    }

    /// <summary>Copia di una via a rulli (stessi parametri di azionamento e geometria): Id e posizione si impostano dopo.</summary>
    private static RollerSection CloneSection(RollerSection src)
    {
        var n = new RollerSection();
        foreach (var pi in typeof(RollerSection).GetProperties())
            if (pi.CanRead && pi.CanWrite && pi.GetIndexParameters().Length == 0) pi.SetValue(n, pi.GetValue(src));
        return n;
    }

    // ------------------------------------------------------------------ Primo rullo dello svincolo

    /// <summary>
    /// Decisione dell'utente (2026-11-18): il primo rullo dello svincolo è sull'inverter della via di legatura (lunghezza del
    /// tratto: un passo rulli). Il pacco che frena oltre la fotocellula di uscita legatura resta su rulli che si fermano con lui.
    /// </summary>
    public static double FirstReleaseRollerMm(RollerSection release) =>
        release.RollerPitchMm > 0 ? release.RollerPitchMm : RollerSection.DefaultPitchMm;

    // ------------------------------------------------------------------ Ultima legatura di coda e accumulo

    /// <summary>
    /// Inizio dell'accumulo (mm): la fotocellula di arresto davanti al trasferitore, oppure l'inizio della via del
    /// trasferitore se viene prima o se la fotocellula manca. <see cref="double.MaxValue"/> se non c'è nessuno dei due.
    /// </summary>
    public static double AccumulationEntryMm(LineProject p)
    {
        var trf = LayoutRules.TransferModule(p);
        double a = trf?.StartMm ?? double.MaxValue;
        double pc = p.Photocells.Where(x => x.IsEnabled && x.ActsAsTransferStop).Select(x => x.PositionMm).DefaultIfEmpty(double.MaxValue).Min();
        return Math.Min(a, pc);
    }

    /// <summary>
    /// Regola dell'utente (2026-11-14): di quanto (mm) la testa del pacco più lungo, durante l'ultima legatura di coda,
    /// supera l'inizio dell'accumulo (fotocellula di arresto davanti al trasferitore). 0 se resta prima.
    /// </summary>
    public static double HeadIntoAccumulationMm(LineProject p)
    {
        double need = RequiredStrappingHeadMaxMm(p);
        double lim = AccumulationEntryMm(p);
        if (double.IsNegativeInfinity(need) || lim == double.MaxValue) return 0;
        return Math.Max(0, need - lim);
    }

    /// <summary>Rulli da aggiungere allo svincolo perché la testa resti prima dell'accumulo con 500 mm di margine.</summary>
    public static int ReleaseRollersForTailStrap(LineProject p)
    {
        double over = HeadIntoAccumulationMm(p);
        var m = ReleaseModules(p).LastOrDefault();
        if (over <= 1e-6 || m == null) return 0;
        return (int)Math.Ceiling((over + ParkingSafetyMm) / Math.Max(1, m.RollerPitchMm) - 1e-9);
    }

    /// <summary>
    /// Allunga lo svincolo (a rulli interi, la linea a valle avanza) finché la testa del pacco più lungo, nell'ultima
    /// legatura di coda, resta prima dell'accumulo. Inverter dedicato con motori commutabili. Falso se non serve o manca lo svincolo.
    /// </summary>
    public static bool LengthenReleaseForTailStrap(LineProject p)
    {
        int rollers = ReleaseRollersForTailStrap(p);
        var m = ReleaseModules(p).LastOrDefault();
        if (rollers <= 0 || m == null) return false;
        double oldEnd = m.EndMm;
        LineReflow.Fit(m, m.StartMm, oldEnd + rollers * m.RollerPitchMm);
        LineReflow.AfterResize(p, m, oldEnd);
        ProjectNormalizer.AlignStopperToLastRoller(p);
        if (!m.JoinStrappingInverter) { m.CommandZoneId = string.Empty; m.FollowsStrapping = true; }
        return true;
    }

    /// <summary>Margini del parcheggio sullo svincolo: frenata prima del punto di arresto (2 m) + sicurezza di manovra (500 mm).</summary>
    public const double ParkingSafetyMm = 500;
    /// <summary>Pacco di riferimento per dimensionare le sezioni-parcheggio dello svincolo lungo (decisione dell'utente, 2026-11-21).</summary>
    public const double ParkingReferenceMm = 12000;

    public static double ParkingMarginMm => PhotocellRules.MinDistanceToTargetMm + ParkingSafetyMm;

    /// <summary>Pacco più corto fra le tipologie del progetto (come la decisione sulla legatura, si guardano tutte).</summary>
    public static double ShortestPackageMm(LineProject p) => p.PackageTypes.Count == 0 ? 0 : p.PackageTypes.Min(t => t.LengthMm);

    /// <summary>Lo svincolo può fare da parcheggio: ci sta il pacco più corto con frenata e margine di manovra.</summary>
    public static bool CanPark(LineProject p, RollerSection release)
    {
        double shortest = ShortestPackageMm(p);
        return shortest > 0 && release.LengthMm >= shortest + ParkingMarginMm - 1e-6;
    }

    /// <summary>Spiegazione leggibile dell'azionamento di un modulo (pagina Ciclo).</summary>
    public static string DescribeDrive(LineProject p, RollerSection m)
    {
        var strap = StrappingModule(p);
        var trf = LayoutRules.TransferModule(p);
        if (ReferenceEquals(m, strap)) return "inverter proprio";
        if (ReferenceEquals(m, trf)) return "inverter proprio (sempre, è il fronte accumulo)";
        string park = CanPark(p, m) ? $"; fa da parcheggio per un pacco fino a {m.LengthMm - ParkingMarginMm:0} mm" : "";
        if (strap != null && m.EffectiveZoneId != strap.EffectiveZoneId && ReferenceEquals(ReleaseModules(p).FirstOrDefault(), m))
            park += $"; il primo rullo ({FirstReleaseRollerMm(m):0} mm) è sull'inverter di legatura";
        if (m.FollowsStrapping) return "inverter dedicato, motori commutati sull'inverter di legatura solo durante le legature di coda" + park;
        if (strap != null && m.EffectiveZoneId == strap.EffectiveZoneId)
            return "unito all'inverter di legatura (un inverter in meno)";
        if (trf != null && m.EffectiveZoneId == trf.EffectiveZoneId)
            return $"unito alla via finale: niente parcheggio, servono {ShortestPackageMm(p) + ParkingMarginMm:0} mm, lo svincolo è {m.LengthMm:0} mm";
        if (m.EffectiveZoneId == m.Id)
            return "inverter dedicato" + park;
        return $"unito all'inverter di {m.EffectiveZoneId}";
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
    public static List<string> KeepReferenceAfterEntry(LineProject p) => EncoderRule.Apply(p);   // regola unica dell'encoder

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
