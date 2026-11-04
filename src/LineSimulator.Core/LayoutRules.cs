using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Regole di layout dell'area di legatura:
/// - una legatrice/reggettatrice non può stare a meno di 3 m dal trasferitore sollevabile;
/// - la via a rulli davanti al trasferitore (l'ultimo modulo, su cui lavora il trasferitore) ha la stessa lunghezza del trasferitore e inizia con esso.
/// </summary>
public static class LayoutRules
{
    /// <summary>Distanza minima tra una legatrice/reggettatrice e l'inizio del trasferitore sollevabile.</summary>
    public const double MinStrapperToTransferMm = 3000;

    /// <summary>Posizione massima ammessa per una legatrice (3 m prima del trasferitore).</summary>
    public static double MaxStrapperPositionMm(LineProject p) => p.Transfer.PositionMm - MinStrapperToTransferMm;

    /// <summary>Legatrici troppo vicine al trasferitore.</summary>
    public static IEnumerable<StrapperConfig> StrappersTooCloseToTransfer(LineProject p) =>
        p.Strappers.Where(s => s.PositionMm > MaxStrapperPositionMm(p) + 1e-6 && s.PositionMm < p.Transfer.EndMm + 1e-6);

    /// <summary>Riporta le legatrici troppo vicine al trasferitore a 3 m da esso. Restituisce le correzioni fatte.</summary>
    public static List<string> ClampStrappers(LineProject p)
    {
        var done = new List<string>();
        double max = MaxStrapperPositionMm(p);
        foreach (var s in StrappersTooCloseToTransfer(p).ToList())
        {
            done.Add($"{s.Id}: {s.PositionMm:0} → {max:0} mm (almeno 3 m dal trasferitore)");
            s.PositionMm = max;
        }
        return done;
    }

    /// <summary>Via a rulli su cui lavora il trasferitore: l'ultimo modulo della linea.</summary>
    public static RollerSection? TransferModule(LineProject p) => p.Sections.OrderBy(s => s.StartMm).LastOrDefault();

    /// <summary>Vero se la via a rulli davanti al trasferitore ha la sua stessa lunghezza e posizione.</summary>
    public static bool TransferModuleMatches(LineProject p)
    {
        var m = TransferModule(p);
        return m == null || (Math.Abs(m.LengthMm - p.Transfer.LengthMm) < 1 && Math.Abs(m.StartMm - p.Transfer.PositionMm) < 1);
    }

    /// <summary>
    /// Porta la via a rulli davanti al trasferitore alla lunghezza del trasferitore (stesso inizio della via) e il trasferitore
    /// all'inizio della via; i bilici seguono il trasferitore e il fermo finale l'ultimo rullo. Restituisce true se ha cambiato qualcosa.
    /// </summary>
    public static bool AlignTransferModule(LineProject p)
    {
        var m = TransferModule(p);
        if (m == null || p.Transfer.LengthMm <= 0 || TransferModuleMatches(p)) return false;
        if (Math.Abs(m.LengthMm - p.Transfer.LengthMm) >= 1)
            LineReflow.Fit(m, m.StartMm, m.StartMm + p.Transfer.LengthMm);
        double dx = m.StartMm - p.Transfer.PositionMm;
        if (Math.Abs(dx) >= 1)
        {
            p.Transfer.PositionMm = m.StartMm;
            foreach (var sc in p.Scales) sc.StartMm += dx;
            foreach (var fs in p.IntermediateStops) fs.PositionMm += dx;
        }
        ProjectNormalizer.AlignStopperToLastRoller(p);
        return true;
    }

    /// <summary>
    /// Posizioni ammesse per il fermo intermedio scomparente: con due bilici solo in mezzo ai due; con un solo bilico al centro
    /// oppure all'estremo opposto al fermo fisso (cioè a monte). Senza bilici nessun vincolo (lista vuota).
    /// </summary>
    public static IReadOnlyList<double> AllowedStopPositionsMm(LineProject p)
    {
        var sc = p.Scales.OrderBy(x => x.StartMm).ToList();
        if (sc.Count == 0) return Array.Empty<double>();
        if (sc.Count == 1) return new[] { sc[0].StartMm, sc[0].StartMm + sc[0].LengthMm / 2 };
        return new[] { (sc[0].StartMm + sc[0].LengthMm + sc[1].StartMm) / 2 };
    }

    /// <summary>Fermi intermedi che non stanno su una posizione ammessa.</summary>
    public static IEnumerable<IntermediateStopConfig> StopsOutOfPlace(LineProject p)
    {
        var allowed = AllowedStopPositionsMm(p);
        if (allowed.Count == 0) return Enumerable.Empty<IntermediateStopConfig>();
        return p.IntermediateStops.Where(f => allowed.All(a => Math.Abs(a - f.PositionMm) >= 0.5));
    }

    /// <summary>Porta i fermi intermedi sulla posizione ammessa più vicina.</summary>
    public static List<string> SnapIntermediateStops(LineProject p)
    {
        var done = new List<string>();
        var allowed = AllowedStopPositionsMm(p);
        if (allowed.Count == 0) return done;
        foreach (var f in p.IntermediateStops)
        {
            double best = allowed.OrderBy(a => Math.Abs(a - f.PositionMm)).First();
            if (Math.Abs(best - f.PositionMm) < 0.5) continue;
            done.Add($"{f.Id}: {f.PositionMm:0} → {best:0} mm (fermo solo tra i bilici o al centro/estremo del bilico)");
            f.PositionMm = best;
        }
        return done;
    }

    /// <summary>Applica tutte le regole (usato dall'app a ogni modifica). Restituisce una descrizione delle correzioni.</summary>
    public static List<string> Enforce(LineProject p)
    {
        var done = new List<string>();
        if (AlignTransferModule(p))
            done.Add($"Via a rulli davanti al trasferitore portata alla lunghezza del trasferitore ({p.Transfer.LengthMm:0} mm)");
        done.AddRange(ClampStrappers(p));
        if (StrapperTrack.ClampStrappersToTrackStart(p))
            done.Add("Legatrice riportata ad almeno 1 m dall'inizio della via rulli");
        done.AddRange(SnapIntermediateStops(p));
        return done;
    }

    /// <summary>Posizioni degli stringifascio: uno per macchina abilitata (se previsto) oppure quello unico.</summary>
    public static IReadOnlyList<(string Id, double PositionMm)> BundleClampPositions(LineProject p)
    {
        var k = p.BundleClamp;
        if (!k.IsEnabled) return Array.Empty<(string, double)>();
        if (!k.PerMachine) return new[] { (k.Id, k.PositionMm) };
        var machines = p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).ToList();
        return machines.Select((s, i) => ($"{k.Id}{i + 1}", s.PositionMm - k.MachineOffsetMm)).ToList();
    }
}
