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
        var stopperPc = ProjectNormalizer.StopperPhotocell(p);   // l'ultima fotocellula prima del fermo segue il fermo
        if (Math.Abs(m.LengthMm - p.Transfer.LengthMm) >= 1)
            LineReflow.Fit(m, m.StartMm, m.StartMm + p.Transfer.LengthMm);
        double dx = m.StartMm - p.Transfer.PositionMm;
        if (Math.Abs(dx) >= 1)
        {
            p.Transfer.PositionMm = m.StartMm;
            foreach (var sc in p.Scales) sc.StartMm += dx;
            foreach (var fs in p.IntermediateStops) fs.PositionMm += dx;
        }
        ProjectNormalizer.AlignStopperToLastRoller(p, stopperPc);
        return true;
    }

    /// <summary>Posizioni ammesse per i fermi scomparenti non manuali: centro o estremità a monte del trasferitore.</summary>
    public static IReadOnlyList<double> AllowedStopPositionsMm(LineProject p) => new[]
    {
        StrappingAreaRules.StopPositionMm(p, DevicePlacement.Center), StrappingAreaRules.StopPositionMm(p, DevicePlacement.End)
    };

    /// <summary>Fermi scomparenti (senza spunta "spostamento manuale") fuori dal centro / estremità scelti.</summary>
    public static IEnumerable<IntermediateStopConfig> StopsOutOfPlace(LineProject p) => StrappingAreaRules.StopsOutOfPlace(p);

    /// <summary>Porta i fermi scomparenti non manuali al centro o all'estremità scelti.</summary>
    public static List<string> SnapIntermediateStops(LineProject p) => StrappingAreaRules.PlaceStops(p);

    /// <summary>La fotocellula di uscita legatura sta mezzo passo dopo l'ultimo rullo della via di legatura, cioè alla sua fine.</summary>
    public static IEnumerable<PhotocellConfig> ExitPhotocellsOutOfPlace(LineProject p)
    {
        var sec = StrapperTrack.LastStrapper(p) is { } ls ? StrapperTrack.SectionAt(p, ls.PositionMm) : null;
        sec ??= p.Sections.OrderBy(x => x.StartMm).FirstOrDefault();
        if (sec == null) return Enumerable.Empty<PhotocellConfig>();
        return p.Photocells.Where(x => x.Purpose == PhotocellPurpose.StrappingExit && Math.Abs(x.PositionMm - sec.EndMm) > 0.5);
    }

    public static bool AlignStrappingExit(LineProject p)
    {
        var list = ExitPhotocellsOutOfPlace(p).ToList();
        if (list.Count == 0) return false;
        var sec = (StrapperTrack.LastStrapper(p) is { } ls ? StrapperTrack.SectionAt(p, ls.PositionMm) : null) ?? p.Sections.OrderBy(x => x.StartMm).First();
        foreach (var x in list)
        {
            x.PositionMm = sec.EndMm;
            if (x.Action == PhotocellAction.Accumulation) x.Action = PhotocellAction.None;   // a fine modulo non c'è spazio di frenata
        }
        return true;
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
        done.AddRange(StrappingAreaRules.Enforce(p));     // macchine sullo stesso lato a 2 m, bilici e fermi sul trasferitore
        // dopo che i fermi sono al loro posto (prima la fotocellula veniva messa rispetto alla vecchia posizione del fermo
        // e serviva una seconda correzione): ogni fermo scomparente ha la sua fotocellula di comando
        done.AddRange(TransferEntryRule.EnsureStopPhotocells(p));
        if (StrapperTrack.KeepEncoderBehindFirstStrapper(p))
            done.Add("Encoder e fotocellula di legatura spostati: restano ad almeno 1 m prima della prima legatrice");
        if (AlignStrappingExit(p))
            done.Add("Fotocellula di uscita legatura portata mezzo passo dopo l'ultimo rullo (fine della via di legatura)");
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
