using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Engine;

/// <summary>
/// Regole di modifica del progetto comuni al programma Windows e alla versione web (2026-11-18): cosa succede dopo la
/// modifica di una proprietà, quali proprietà sono bloccate, quali scelte hanno i campi "Id", il riordino del progetto
/// dopo ogni modifica e l'aggiunta, la duplicazione e l'eliminazione dei componenti.
/// </summary>
public static class ProjectEditing
{
    // ------------------------------------------------------------------ Riordino dopo ogni modifica

    /// <summary>
    /// Riordino del progetto dopo ogni modifica (lo stesso del programma Windows): fotocellula di ingresso, fotocellule sulle
    /// vie, nome e descrizione, famiglie, fermo a fine linea, via minima dopo l'ultima legatrice, regole di layout e di
    /// intestatura. Restituisce i messaggi da mostrare.
    /// </summary>
    public static List<string> Normalize(LineProject p)
    {
        var msgs = new List<string>();
        ProjectNormalizer.AlignEntryPhotocell(p);
        ProjectNormalizer.ClampPhotocellsToRollers(p);
        ProjectDescription.Refresh(p);
        GroupInheritance.PropagateAll(p);
        RollerGroups.Propagate(p, applyPitch: false);
        ProjectNormalizer.AlignStopperToLastRoller(p);
        double extraTrack = StrapperTrack.EnsureMinimumTrack(p);
        if (extraTrack > 0) msgs.Add($"Via a rulli allungata di {extraTrack:0} mm: dopo l'ultima legatrice servono almeno due rulli di via.");
        msgs.AddRange(LayoutRules.Enforce(p));
        if (BankingOptions.Normalize(p)) msgs.Add("Intestatura di coda non disponibile senza fermo scomparente: impostata di testa");
        return msgs;
    }

    // ------------------------------------------------------------------ Proprietà

    /// <summary>Componente con posizione fissa: fotocellula di ingresso, primo modulo a inizio linea.</summary>
    public static bool IsFixed(LineProject p, object? m) => m switch
    {
        PhotocellConfig pc => pc.IsEntryPhotocell,
        RollerSection s => p.Sections.Count > 0 && ReferenceEquals(s, p.Sections.OrderBy(x => x.StartMm).First()) && s.StartMm <= 0.5,
        _ => false
    };

    /// <summary>Proprietà bloccata: ereditata dalla famiglia (componente non personalizzato) o fissata da una regola.</summary>
    public static bool IsLocked(LineProject p, object model, string property) => model switch
    {
        RollerSection s => (!s.OverrideGroup && RollerGroups.InheritedProperties.Contains(property))
                           || (IsFixed(p, s) && property == nameof(RollerSection.StartMm)),
        PhotocellConfig pc => (pc.IsEntryPhotocell && property == nameof(PhotocellConfig.PositionMm))
                              || property == nameof(PhotocellConfig.Purpose)
                              || (!pc.OverrideGroup && GroupInheritance.InheritedNames(p.PhotocellDefaults).Contains(property)),
        StrapperConfig st => !st.OverrideGroup && GroupInheritance.InheritedNames(p.StrapperDefaults).Contains(property),
        WeighingScaleConfig sc => !sc.OverrideGroup && GroupInheritance.InheritedNames(p.ScaleDefaults).Contains(property),
        _ => false
    };

    /// <summary>Scelte possibili per i campi che indicano un altro componente (null = campo libero).</summary>
    public static IReadOnlyList<string>? IdOptions(LineProject p, string property) => property switch
    {
        "InverterId" or "ControlledInverterId" or "TargetInverterId" => p.Sections.OrderBy(i => i.StartMm).Select(i => i.Id).ToList(),
        "PackageTypeId" => p.PackageTypes.Select(i => i.Id).ToList(),
        _ => null
    };

    /// <summary>
    /// Dopo la modifica della proprietà <paramref name="property"/> di <paramref name="primary"/> (e degli altri componenti
    /// selezionati dello stesso tipo): lunghezze delle vie a rulli e reflow della linea, arrotondamenti, propagazione dalle
    /// famiglie. True se la modifica cambia la struttura della linea.
    /// </summary>
    public static bool AfterEdit(LineProject p, object primary, string property, IReadOnlyList<object>? selection = null)
    {
        var selected = selection is { Count: > 0 } ? selection : new[] { primary };
        if (property is not ("Id" or "Name"))
        {
            var others = selected.Where(o => !ReferenceEquals(o, primary) && o.GetType() == primary.GetType()).ToList();
            var pi = primary.GetType().GetProperty(property);
            if (others.Count > 0 && pi != null) MultiEdit.Apply(others, pi, pi.GetValue(primary));
        }
        // vie a rulli: si impostano solo lunghezza (multipla di 50 mm) e numero rulli; il passo ne segue. Le vie restano unite.
        foreach (var rs in selected.OfType<RollerSection>().Where(_ => property is "RollerCount" or "RollerPitchMm" or "LengthMm"))
        {
            var nextSec = p.Sections.Where(x => !ReferenceEquals(x, rs) && x.StartMm >= rs.StartMm + 1).OrderBy(x => x.StartMm).FirstOrDefault();
            double oldEnd = nextSec?.StartMm ?? p.Stopper.PositionMm;
            rs.RollerCount = Math.Max(2, rs.RollerCount);
            if (property == "LengthMm")
            {
                rs.LengthMm = Math.Max(2 * LineReflow.PitchStepMm, Math.Round(rs.LengthMm / LineReflow.PitchStepMm) * LineReflow.PitchStepMm);
                rs.RollerPitchMm = rs.LengthMm / rs.RollerCount;
            }
            else if (property == "RollerCount") rs.RollerPitchMm = rs.LengthMm / rs.RollerCount;
            rs.RecalculateLength();
            LineReflow.AfterResize(p, rs, oldEnd);
            if (nextSec == null && property == "LengthMm" && Math.Abs(p.Transfer.LengthMm - rs.LengthMm) >= 1)
            {
                // ultimo modulo = via davanti al trasferitore: il trasferitore ne segue la lunghezza
                p.Transfer.Sections.Clear();
                p.Transfer.LengthMm = rs.LengthMm;
                p.Transfer.PositionMm = rs.StartMm;
            }
        }
        if (property == nameof(RollerSection.RollerPitchMm))
            foreach (var m in selected.Where(x => x is RollerSection or RollerGroup))
            {
                if (m is RollerSection r1) r1.RollerPitchMm = LineReflow.RoundPitch(r1.RollerPitchMm);
                else if (m is RollerGroup g1) g1.RollerPitchMm = LineReflow.RoundPitch(g1.RollerPitchMm);
            }
        if (property is "LengthMm" or "FootprintLengthMm" && primary is TransferConfig or WeighingScaleConfig or StrapperConfig or StrapperGroup)
            foreach (var m in selected)
            {
                var lp = m.GetType().GetProperty(property);
                if (lp?.GetValue(m) is double len && lp.CanWrite) lp.SetValue(m, Math.Max(100, Math.Round(len / 100) * 100));
            }
        bool groupChanged = false;
        if (primary is RollerGroup)
            groupChanged = RollerGroups.Propagate(p, applyPitch: property == nameof(RollerGroup.RollerPitchMm));
        if (primary is RollerGroup && property == nameof(RollerGroup.RollerDiameterMm))
        {
            RollerGroups.SetDiameterForAll(p);
            groupChanged = true;
        }
        if (primary is RollerGroup && property == nameof(RollerGroup.RollerTopElevationMm))
            RollerGroups.SyncElevation(p, fromGroup: true);
        if (primary is PhotocellGroup or StrapperGroup or ScaleGroup)
            groupChanged |= GroupInheritance.PropagateAll(p);
        else if (primary is IGroupChild && property == "OverrideGroup")
            groupChanged |= GroupInheritance.PropagateAll(p);
        if (primary is RollerSection && property == nameof(RollerSection.OverrideGroup))
            groupChanged = RollerGroups.Propagate(p, applyPitch: true);
        bool structural = property is "Id" or "StartMm" or "PositionMm" or "DistanceFromLastRollerMm" or "InverterId" or "IsEnabled"
                          or "RollerCount" or "RollerPitchMm" or "EndOverhangMm" or "LengthMm";
        return structural || groupChanged;
    }

    /// <summary>
    /// Cambia l'Id di un componente aggiornando tutti i riferimenti (zone di comando, fotocellule, legatrici, regole, piani,
    /// sequenza d'ingresso, dispositivi: revisore C2). Restituisce il motivo se l'Id non è ammesso, altrimenti null.
    /// </summary>
    public static string? RenameId(LineProject p, ComponentBase component, string newId)
    {
        newId = (newId ?? string.Empty).Trim();
        if (newId.Length == 0) return "L'identificativo non può essere vuoto.";
        string old = component.Id;
        if (string.Equals(old, newId, StringComparison.Ordinal)) return null;
        if (AllComponents(p).Any(x => !ReferenceEquals(x, component) && string.Equals(x.Id, newId, StringComparison.Ordinal)))
            return $"L'identificativo {newId} è già usato da un altro componente.";
        SimulationPreparation.RemapReferences(p, new Dictionary<string, string> { [old] = newId });
        component.Id = newId;
        return null;
    }

    // ------------------------------------------------------------------ Componenti

    /// <summary>Tutti i componenti con un Id (per gli Id progressivi).</summary>
    public static IEnumerable<ComponentBase> AllComponents(LineProject p) =>
        p.Sections.Cast<ComponentBase>().Concat(p.Photocells).Concat(p.Strappers).Concat(p.PackageTypes).Concat(p.Rules)
            .Concat(p.IntermediateStops).Concat(p.Scales);

    /// <summary>Primo Id libero con il prefisso indicato (M1, M2, …).</summary>
    public static string NextId(LineProject p, string prefix)
    {
        var ids = AllComponents(p).Select(e => e.Id).ToHashSet();
        int n = 1;
        while (ids.Contains(prefix + n)) n++;
        return prefix + n;
    }

    /// <summary>Componenti che si possono aggiungere, con l'etichetta da mostrare.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Addable = new[]
    {
        ("section", "Modulo via a rulli"), ("photocell", "Fotocellula"), ("strapper", "Legatrice"),
        ("stop-center", "Fermo scomparente al centro del trasferitore"), ("stop-end", "Fermo scomparente all'estremità del trasferitore"),
        ("scale-center", "Bilico al centro del trasferitore"), ("scale-end", "Bilico all'estremità del trasferitore"),
        ("package", "Tipologia di pacco"), ("rule", "Regola logica"),
        ("clamp", "Stringifascio"), ("compactor", "Compattatore laterale"), ("rejector", "Rigettatrice")
    };

    /// <summary>Aggiunge un componente (chiave di <see cref="Addable"/>) e restituisce il nuovo oggetto (null se non aggiunto).</summary>
    public static object? Add(LineProject p, string key)
    {
        object? added = null;
        switch (key)
        {
            case "section":
            {
                var last = p.Sections.OrderBy(x => x.StartMm).LastOrDefault();
                var s = new RollerSection { Id = NextId(p, "M"), StartMm = last?.EndMm ?? 0, RollerCount = 6 };
                s.Name = "Modulo " + s.Id;
                RollerGroups.CopyDrive(p.SectionDefaults, s);
                s.RollerPitchMm = p.SectionDefaults.RollerPitchMm;
                s.RecalculateLength();
                p.Sections.Add(s);
                added = s;
                break;
            }
            case "photocell":
            {
                var topo = new LineTopology(p);
                double mid = (topo.LineStartM + topo.LineEndM) / 2 * 1000;
                var pc = new PhotocellConfig { Id = NextId(p, "FC"), Name = "Nuova fotocellula", PositionMm = Math.Round(mid) };
                p.Photocells.Add(pc);
                PhotocellRoles.Decide(p, pc);
                added = pc;
                break;
            }
            case "strapper":
            {
                if (p.Strappers.Count >= LineProject.MaxStrappers) return null;
                double pos = p.Strappers.Count > 0 ? p.Strappers.Max(s => s.PositionMm) + 4500 : 30000;
                var s = new StrapperConfig { Id = NextId(p, "L"), Name = $"Legatrice {p.Strappers.Count + 1}", PositionMm = pos };
                p.Strappers.Add(s);
                added = s;
                break;
            }
            case "stop-center" or "stop-end":
            {
                var placement = key == "stop-end" ? DevicePlacement.End : DevicePlacement.Center;
                var s = new IntermediateStopConfig
                {
                    Id = NextId(p, "FM"), Name = "Fermo scomparente", Placement = placement, PositionMm = StrappingAreaRules.StopPositionMm(p, placement)
                };
                p.IntermediateStops.Add(s);
                if (p.Transfer.BatchSize < 1 + p.IntermediateStops.Count(x => x.IsEnabled))
                    p.Transfer.BatchSize = 1 + p.IntermediateStops.Count(x => x.IsEnabled);
                added = s;
                break;
            }
            case "scale-center" or "scale-end":
            {
                var placement = key == "scale-end" ? DevicePlacement.End : DevicePlacement.Center;
                var s = new WeighingScaleConfig { Id = NextId(p, "B"), Name = "Bilico", Placement = placement, StartMm = p.Transfer.PositionMm, LengthMm = 6000 };
                p.Scales.Add(s);
                StrappingAreaRules.LayoutScales(p);
                added = s;
                break;
            }
            case "package":
            {
                // stessa regola per Windows e web: 3 m più lunga dell'ultima tipologia, Id dalla regola dei nomi, in sequenza d'ingresso
                var last = p.PackageTypes.OrderBy(x => x.LengthMm).LastOrDefault();
                int m = last == null ? 6 : (int)Math.Min(30, Math.Round(last.LengthMm / 1000) + 3);
                var t = SampleProjects.Package(m);
                t.Id = PackageNaming.UniqueId(p, t);
                p.PackageTypes.Add(t);
                p.Infeed.TypeSequence = p.PackageTypes.Select(x => x.Id).ToList();
                added = t;
                break;
            }
            case "rule":
            {
                var r = new LogicRule
                {
                    Id = NextId(p, "R"), Name = "Nuova regola", TargetInverterId = p.Sections.OrderBy(x => x.StartMm).FirstOrDefault()?.Id ?? "",
                    Action = RuleAction.SetLowSpeed, Condition = new ConstantCondition { Value = false }
                };
                p.Rules.Add(r);
                added = r;
                break;
            }
            case "clamp": p.BundleClamp.IsEnabled = true; added = p.BundleClamp; break;
            case "compactor": p.Compactor.IsEnabled = true; added = p.Compactor; break;
            case "rejector": p.Rejector.IsEnabled = true; added = p.Rejector; break;
        }
        if (added != null) SimulationPreparation.RenumberIds(p);
        return added;
    }

    /// <summary>Componenti che si possono eliminare (stringifascio, compattatore e rigettatrice si disabilitano).</summary>
    public static bool CanDelete(object? m) => m is RollerSection or PhotocellConfig or StrapperConfig or PackageType or LogicRule
        or IntermediateStopConfig or WeighingScaleConfig or BundleClampConfig or SideCompactorConfig or RejectorConfig;

    /// <summary>
    /// La via di legatura (la prima) e quella davanti al trasferitore (M1 e M3 negli esempi) non si tolgono mai; lo svincolo non si
    /// toglie se è rimasto l'unico (regola dell'utente del 2026-11-22).
    /// </summary>
    public static bool IsProtectedSection(LineProject p, object? m) =>
        m is RollerSection s && (ReferenceEquals(s, p.Sections.OrderBy(x => x.StartMm).FirstOrDefault()) || ReferenceEquals(s, LayoutRules.TransferModule(p))
            || (StrappingAreaRules.ReleaseModules(p) is { Count: 1 } rel && ReferenceEquals(rel[0], s)));

    public static bool CanDelete(LineProject p, object? m) => CanDelete(m) && !IsProtectedSection(p, m);

    /// <summary>Elimina i componenti: le vie a rulli da valle a monte, e la linea si richiude.</summary>
    public static void Delete(LineProject p, IEnumerable<object> models)
    {
        foreach (var m in models.Where(x => CanDelete(p, x)).OrderByDescending(x => x is RollerSection rs ? rs.StartMm : 0).ToList())
        {
            switch (m)
            {
                case RollerSection s:
                {
                    var rel = StrappingAreaRules.ReleaseModules(p);
                    p.Sections.Remove(s);
                    LineReflow.CloseGap(p, s.StartMm, s.EndMm);
                    // C8: svincolo già diviso in sezioni dal programma → le sezioni rimaste si riuniscono e la preparazione lo
                    // ridivide dalla nuova lunghezza totale (le sezioni le decide il simulatore)
                    if (rel.Count > 1 && rel.Contains(s)) StrappingAreaRules.MergeSplitRelease(p, rel);
                    break;
                }
                case PhotocellConfig pc: p.Photocells.Remove(pc); break;
                case StrapperConfig st: p.Strappers.Remove(st); break;
                case PackageType tp:
                    p.PackageTypes.Remove(tp);
                    p.Infeed.TypeSequence = p.Infeed.TypeSequence.Where(x => x != tp.Id).ToList();
                    break;
                case LogicRule r: p.Rules.Remove(r); break;
                case IntermediateStopConfig fs: p.IntermediateStops.Remove(fs); break;
                case WeighingScaleConfig sc: p.Scales.Remove(sc); StrappingAreaRules.LayoutScales(p); break;
                case BundleClampConfig k: k.IsEnabled = false; break;
                case SideCompactorConfig cm: cm.IsEnabled = false; break;
                case RejectorConfig rj: rj.IsEnabled = false; break;
            }
        }
        SimulationPreparation.RenumberIds(p);
    }
}
