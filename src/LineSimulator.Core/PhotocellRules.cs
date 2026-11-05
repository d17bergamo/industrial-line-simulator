using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Regole delle fotocellule di processo (sessione B), controllate prima di avviare la simulazione:
/// - le fotocellule che rallentano o fermano stanno ad almeno 2 m dal punto di arresto (target), e di più se lo spazio di
///   frenata dalla velocità massima della zona lo richiede (<see cref="RequiredDistanceMm"/>);
/// - la fotocellula di uscita della via rulli di legatura e quella di arresto davanti al trasferitore, se sono vicine,
///   vengono unite in una sola fotocellula con i due ruoli.
/// </summary>
public static class PhotocellRules
{
    /// <summary>Distanza minima tra una fotocellula che rallenta o ferma e il suo punto di arresto (anche se il calcolo dà meno).</summary>
    public const double MinDistanceToTargetMm = 2000;

    /// <summary>
    /// Distanza richiesta tra la fotocellula e il punto di arresto: il massimo tra 2 m e lo spazio per fermarsi (o rallentare
    /// alla velocità bassa, per le fotocellule di rallentamento) arrivando alla velocità massima della zona comandata:
    /// tempo di risposta × v + (v² − v_fine²) / (2 × decelerazione) + distanza di sicurezza.
    /// </summary>
    public static double RequiredDistanceMm(LineProject p, PhotocellConfig pc, LineTopology? topo = null)
    {
        topo ??= new LineTopology(p);
        var zone = topo.Zone(pc.ControlledInverterId) ?? topo.ZoneAt(Units.MmToM(pc.PositionMm));
        var secs = zone?.Sections ?? (IReadOnlyList<RollerSection>)p.Sections;
        if (secs.Count == 0) return MinDistanceToTargetMm;
        double v = secs.Max(s => Math.Max(s.MaxSpeed, s.HighSpeed));
        double a = secs.Min(s => s.Deceleration);
        if (a <= 0) return double.PositiveInfinity;
        double vEnd = pc.Action == PhotocellAction.SlowDown && !pc.ActsAsTransferStop ? Math.Min(v, secs.Min(s => s.LowSpeed)) : 0;
        double brake = Units.MsToS(pc.ResponseTimeMs) * v + (v * v - vEnd * vEnd) / (2 * a);
        return Math.Max(MinDistanceToTargetMm, Math.Ceiling((brake * 1000 + p.Simulation.MinSafetyDistanceMm) / 10) * 10);
    }

    /// <summary>Distanza entro cui uscita legatura e arresto davanti al trasferitore vengono unite.</summary>
    public const double MergeDistanceMm = 1000;

    /// <summary>
    /// Punto di arresto comandato dalla fotocellula (mm), null se non rallenta né ferma:
    /// arresto davanti al trasferitore → inizio del trasferitore; rallentamento nella zona del fermo finale → fermo finale;
    /// rallentamento, arresto o accumulo nelle altre zone → fine della zona comandata.
    /// </summary>
    public static double? TargetMm(LineProject p, PhotocellConfig pc, LineTopology? topo = null)
    {
        if (!pc.IsEnabled || pc.IsEntryPhotocell) return null;
        if (pc.ActsAsTransferStop) return p.Transfer.LengthMm > 0 ? p.Transfer.PositionMm : null;
        if (pc.Action is not (PhotocellAction.SlowDown or PhotocellAction.Stop or PhotocellAction.Accumulation)) return null;
        topo ??= new LineTopology(p);
        var zone = topo.Zone(pc.ControlledInverterId) ?? topo.ZoneAt(Units.MmToM(pc.PositionMm));
        if (zone == null) return null;
        double x = Units.MmToM(pc.PositionMm);
        if (pc.Action == PhotocellAction.SlowDown && topo.StopperZone == zone && pc.PositionMm < p.Stopper.PositionMm)
            return p.Stopper.PositionMm;
        if (x < zone.StartM - 1e-9 || x > zone.EndM + 1e-9) return null;   // fotocellula fuori dalla sua zona: altre regole
        return Units.MToMm(zone.EndM);
    }

    /// <summary>Fotocellule che rallentano o fermano più vicine al punto di arresto della distanza richiesta.</summary>
    public static IEnumerable<(PhotocellConfig Pc, double TargetMm, double DistanceMm, double RequiredMm)> TooCloseToTarget(LineProject p)
    {
        var topo = new LineTopology(p);
        foreach (var pc in p.Photocells)
        {
            if (TargetMm(p, pc, topo) is not { } t) continue;
            double d = t - pc.PositionMm, need = RequiredDistanceMm(p, pc, topo);
            if (d < need - 0.5) yield return (pc, t, d, need);
        }
    }

    /// <summary>
    /// Porta a 2 m dal punto di arresto le fotocellule troppo vicine (spostandole a monte, mai prima di FC0 + 100 mm).
    /// Restituisce le correzioni.
    /// </summary>
    public static List<string> KeepDistanceFromTarget(LineProject p)
    {
        var done = new List<string>();
        double min = StrappingAreaRules.MinReferencePhotocellMm(p);
        foreach (var (pc, t, _, need) in TooCloseToTarget(p).ToList())
        {
            double x = Math.Max(min, Math.Floor((t - need) / 10) * 10);
            if (Math.Abs(x - pc.PositionMm) < 0.5) continue;
            done.Add($"{pc.Id}: {pc.PositionMm:0} → {x:0} mm ({need:0} mm dal punto di arresto a {t:0} mm per fermarsi dalla velocità massima)");
            pc.PositionMm = x;
        }
        return done;
    }

    /// <summary>Coppia uscita legatura / arresto davanti al trasferitore da unire (null se non ci sono o non sono vicine).</summary>
    public static (PhotocellConfig Exit, PhotocellConfig Stop)? MergeCandidate(LineProject p)
    {
        var exit = p.Photocells.FirstOrDefault(x => x.Purpose == PhotocellPurpose.StrappingExit && x.IsEnabled);
        var stop = p.Photocells.FirstOrDefault(x => x.Purpose == PhotocellPurpose.TransferStop && x.IsEnabled && x != exit);
        if (exit == null || stop == null || exit.AlsoTransferStop) return null;
        if (Math.Abs(exit.PositionMm - stop.PositionMm) > MergeDistanceMm + 0.5) return null;
        // l'uscita legatura resta alla fine della via di legatura: si unisce solo se da lì l'arresto davanti al trasferitore è ancora in tempo
        if (p.Transfer.PositionMm - exit.PositionMm < ProjectNormalizer.TransferStopDistanceMm(p) - 1) return null;
        return (exit, stop);
    }

    /// <summary>
    /// Unisce la fotocellula di uscita legatura e quella di arresto davanti al trasferitore se distano al massimo 1 m:
    /// resta la fotocellula di uscita (alla fine della via di legatura) con anche il ruolo di arresto. Restituisce le correzioni.
    /// </summary>
    public static List<string> MergeExitWithTransferStop(LineProject p)
    {
        var done = new List<string>();
        if (MergeCandidate(p) is not var (exit, stop)) return done;
        exit.AlsoTransferStop = true;
        exit.ControlledInverterId = stop.ControlledInverterId;
        p.Photocells.Remove(stop);
        done.Add($"{stop.Id} (arresto davanti al trasferitore) unita a {exit.Id} (uscita legatura): distavano {Math.Abs(exit.PositionMm - stop.PositionMm):0} mm, ora una sola fotocellula a {exit.PositionMm:0} mm");
        return done;
    }

    /// <summary>Controlli prima di avviare la simulazione: unione delle fotocellule vicine e distanza di 2 m dal punto di arresto.</summary>
    public static List<string> BeforeSimulation(LineProject p)
    {
        var done = new List<string>();
        done.AddRange(MergeExitWithTransferStop(p));
        done.AddRange(KeepDistanceFromTarget(p));
        return done;
    }
}
