using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Regola unica per l'encoder di legatura e la sua fotocellula di riferimento (prima erano quattro regole diverse in
/// normalizzatore, preparazione del ciclo, via di legatura e area di legatura):
/// <list type="bullet">
/// <item>l'encoder sta sul primo rullo a monte dell'ingombro della prima legatrice: posizione = piano di legatura − (metà ingombro + passo rulli);</item>
/// <item>comunque ad almeno 1 m a monte del piano della prima legatrice;</item>
/// <item>la fotocellula di riferimento sta 300 mm prima dell'encoder e mai a monte di FC0 + 100 mm
///   (se la legatrice è troppo vicina all'inizio vince questo limite e la scheda Controllo lo segnala).</item>
/// </list>
/// Encoder e fotocellula di riferimento seguono sempre la regola, anche se spostati a mano.
/// </summary>
public static class EncoderRule
{
    /// <summary>Distanza della fotocellula di riferimento a monte dell'encoder.</summary>
    public const double ReferenceOffsetMm = 300;
    /// <summary>Distanza minima tra l'encoder e il piano di legatura della prima legatrice.</summary>
    public const double MinToStrapperMm = 1000;
    /// <summary>Tolleranza di posizione (mm).</summary>
    public const double TolMm = 0.5;

    /// <summary>Prima macchina di legatura abilitata (null se la stazione è disattivata o senza macchine).</summary>
    public static StrapperConfig? FirstStrapper(LineProject p) =>
        p.StrappingStation.IsEnabled ? p.Strappers.Where(s => s.IsEnabled).OrderBy(s => s.PositionMm).FirstOrDefault() : null;

    /// <summary>Posizione dell'encoder secondo la regola, null se non serve (nessuna legatrice).</summary>
    public static double? TargetMm(LineProject p)
    {
        var first = FirstStrapper(p);
        if (first == null) return null;
        var host = p.Sections.FirstOrDefault(s => first.PositionMm >= s.StartMm - 1e-6 && first.PositionMm <= s.EndMm + 1e-6);
        double pitch = host?.RollerPitchMm ?? RollerSection.DefaultPitchMm;
        double ideal = first.PositionMm - Math.Max(MinToStrapperMm, first.FootprintLengthMm / 2 + pitch);
        double min = StrappingAreaRules.MinReferencePhotocellMm(p) + ReferenceOffsetMm;
        return Math.Round(Math.Max(ideal, min));
    }

    /// <summary>Vera se la prima legatrice è così vicina all'inizio linea che l'encoder non può stare ad almeno 1 m da essa.</summary>
    public static bool StrapperTooCloseToStart(LineProject p)
    {
        var first = FirstStrapper(p);
        return first != null && TargetMm(p) is double t && first.PositionMm - t < MinToStrapperMm - TolMm;
    }

    /// <summary>Scostamenti dalla regola (vuoto = tutto a posto).</summary>
    public static List<string> Problems(LineProject p)
    {
        var r = new List<string>();
        if (TargetMm(p) is not double enc) return r;
        if (!p.StrappingEncoder.IsEnabled) { r.Add("manca l'encoder di legatura"); return r; }
        if (Math.Abs(p.StrappingEncoder.PositionMm - enc) > TolMm)
            r.Add($"encoder {p.StrappingEncoder.Id} a {p.StrappingEncoder.PositionMm:0} mm invece di {enc:0} mm");
        foreach (var pc in p.Photocells.Where(x => x.Purpose == PhotocellPurpose.StrappingReference))
            if (Math.Abs(pc.PositionMm - (enc - ReferenceOffsetMm)) > TolMm)
                r.Add($"fotocellula di riferimento {pc.Id} a {pc.PositionMm:0} mm invece di {enc - ReferenceOffsetMm:0} mm");
        if (StrapperTooCloseToStart(p))
            r.Add($"la prima legatrice è troppo vicina all'inizio linea: l'encoder resta a meno di {MinToStrapperMm:0} mm da essa");
        return r;
    }

    /// <summary>Porta encoder e fotocellule di riferimento nella posizione della regola. Restituisce le correzioni.</summary>
    public static List<string> Apply(LineProject p)
    {
        var done = new List<string>();
        if (TargetMm(p) is not double enc || !p.StrappingEncoder.IsEnabled) return done;
        if (Math.Abs(p.StrappingEncoder.PositionMm - enc) > TolMm)
        {
            done.Add($"{p.StrappingEncoder.Id}: encoder {p.StrappingEncoder.PositionMm:0} → {enc:0} mm (regola dell'encoder: primo rullo a monte della prima legatrice, almeno 1 m prima di essa)");
            p.StrappingEncoder.PositionMm = enc;
        }
        double x = enc - ReferenceOffsetMm;
        foreach (var pc in p.Photocells.Where(x => x.Purpose == PhotocellPurpose.StrappingReference))
        {
            if (Math.Abs(pc.PositionMm - x) <= TolMm) continue;
            done.Add($"{pc.Id}: fotocellula di riferimento {pc.PositionMm:0} → {x:0} mm (300 mm prima dell'encoder)");
            pc.PositionMm = x;
        }
        return done;
    }
}
