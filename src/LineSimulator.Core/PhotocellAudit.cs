using System.Text.RegularExpressions;
using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Esito del controllo di una fotocellula.</summary>
public enum PhotocellVerdict
{
    /// <summary>Ha una funzione e non ha un punto di arresto da rispettare (ingresso, riferimento, uscita, segnale usato).</summary>
    Ok,
    /// <summary>Alla distanza minima dal suo punto di arresto (entro 50 mm).</summary>
    AtMinimum,
    /// <summary>Più lontana del necessario dal punto di arresto: si potrebbe avvicinare.</summary>
    AboveMinimum,
    /// <summary>Più vicina del minimo: il pacco non fa in tempo a frenare.</summary>
    BelowMinimum,
    /// <summary>Non ha funzione di processo e nessuna regola la usa, oppure doppia di un'altra con la stessa funzione.</summary>
    Superfluous
}

/// <summary>Riga del controllo: perché c'è la fotocellula, punto di arresto, distanza effettiva e richiesta, esito.</summary>
public sealed record PhotocellAuditRow(PhotocellConfig Photocell, string Role, string? TargetName, double? DistanceMm, double? RequiredMm,
    PhotocellVerdict Verdict, string Note);

/// <summary>
/// Controllo delle fotocellule (decisione dell'utente del 2026-11-13, opzione "b"): per ogni fotocellula spiega perché c'è,
/// se è alla distanza minima dal suo punto di arresto e segnala quelle superflue. Non sposta né toglie nulla.
/// </summary>
public static class PhotocellAudit
{
    /// <summary>Tolleranza per "alla distanza minima".</summary>
    public const double AtMinimumToleranceMm = 50;
    /// <summary>Due fotocellule con la stessa funzione più vicine di così sono doppie.</summary>
    public const double DuplicateDistanceMm = 100;

    /// <summary>Funzione della fotocellula in parole semplici.</summary>
    public static string Role(PhotocellConfig pc)
    {
        if (pc.IsEntryPhotocell) return "Ingresso (FC0): identifica e misura il pacco, consenso di ingresso";
        if (!string.IsNullOrEmpty(pc.CommandsStopId) && !pc.ActsAsTransferStop)
            return pc.CommandsStopId.EndsWith(TransferEntryRule.TailKeySuffix, StringComparison.Ordinal)
                ? $"Intestatura di coda: ferma il pacco e solleva il fermo {pc.CommandsStopId[..^TransferEntryRule.TailKeySuffix.Length]}"
                : $"Comando del fermo scomparente {pc.CommandsStopId}";
        string r = pc.Purpose switch
        {
            PhotocellPurpose.StrappingReference => "Riferimento encoder: posizione del pacco per le legatrici",
            PhotocellPurpose.StrappingExit => "Uscita legatura: liberata dalla coda, parte il pacco successivo",
            PhotocellPurpose.TransferStop => "Arresto davanti al trasferitore (non a riposo o area occupata)",
            _ => pc.Action switch
            {
                PhotocellAction.SlowDown => "Rallentamento prima del punto di arresto",
                PhotocellAction.Stop => "Arresto del modulo comandato",
                PhotocellAction.Accumulation => "Accumulo: ferma se il modulo a valle non accetta",
                PhotocellAction.SpeedUp => "Velocità alta sul modulo comandato",
                _ => "Solo segnale"
            }
        };
        return pc.AlsoTransferStop && pc.Purpose != PhotocellPurpose.TransferStop ? r + " + arresto davanti al trasferitore" : r;
    }

    public static List<PhotocellAuditRow> Run(LineProject p)
    {
        var pcs = p.Photocells.Where(x => x.IsEnabled).OrderBy(x => x.PositionMm).ToList();
        var rows = new List<PhotocellAuditRow>();
        string refs = ReferencedText(p);
        foreach (var pc in pcs)
        {
            string role = Role(pc);
            double? target = PhotocellRules.TargetMm(p, pc);
            if (target is double t)
            {
                double dist = t - pc.PositionMm, req = PhotocellRules.RequiredDistanceMm(p, pc);
                string tn = TargetName(p, t);
                var v = dist < req - 0.5 ? PhotocellVerdict.BelowMinimum
                    : dist <= req + AtMinimumToleranceMm ? PhotocellVerdict.AtMinimum : PhotocellVerdict.AboveMinimum;
                string note = v switch
                {
                    PhotocellVerdict.BelowMinimum => $"troppo vicina: mancano {req - dist:0} mm per frenare (si corregge all'avvio della simulazione)",
                    PhotocellVerdict.AtMinimum => "alla distanza minima",
                    _ => $"{dist - req:0} mm oltre il minimo: si può avvicinare al punto di arresto"
                };
                rows.Add(new(pc, role, tn, dist, req, v, note));
                continue;
            }
            bool process = pc.IsEntryPhotocell || pc.Purpose != PhotocellPurpose.Generic || pc.Action != PhotocellAction.None
                           || !string.IsNullOrEmpty(pc.CommandsStopId);
            bool used = Regex.IsMatch(refs, $@"\b{Regex.Escape(pc.Id)}\b");
            rows.Add(process || used
                ? new(pc, role, null, null, null, PhotocellVerdict.Ok, used && !process ? "usata dalle regole logiche" : "")
                : new(pc, role, null, null, null, PhotocellVerdict.Superfluous, "non ha funzione di processo e nessuna regola la usa: si può togliere"));
        }
        // Doppie: stessa funzione a meno di 100 mm (resta la prima a monte)
        for (int i = 1; i < rows.Count; i++)
        {
            var a = rows[i - 1]; var b = rows[i];
            if (b.Verdict == PhotocellVerdict.Superfluous || a.Role != b.Role) continue;
            if (b.Photocell.PositionMm - a.Photocell.PositionMm < DuplicateDistanceMm)
                rows[i] = b with { Verdict = PhotocellVerdict.Superfluous, Note = $"doppia di {a.Photocell.Id} ({b.Photocell.PositionMm - a.Photocell.PositionMm:0} mm, stessa funzione): si può togliere" };
        }
        return rows;
    }

    /// <summary>Testi delle regole logiche e delle condizioni dei dispositivi, dove una fotocellula "solo segnale" può essere usata.</summary>
    private static string ReferencedText(LineProject p)
    {
        var parts = new List<string>();
        foreach (var r in p.Rules.Where(r => r.IsEnabled))
        {
            try { parts.Add(ConditionText.Format(r.Condition)); } catch { /* condizione non formattabile */ }
            parts.Add(r.SignalName);
        }
        foreach (var d in new SequenceDeviceConfig[] { p.BundleClamp, p.Compactor, p.Rejector }.Concat(p.Scales))
        {
            parts.Add(d.StartConsent);
            parts.Add(d.StopCondition);
        }
        return string.Join(" ", parts);
    }

    private static string TargetName(LineProject p, double mm)
    {
        if (p.Transfer.LengthMm > 0 && Math.Abs(mm - p.Transfer.PositionMm) < 1) return "inizio trasferitore";
        if (Math.Abs(mm - p.Stopper.PositionMm) < 1) return "fermo finale";
        var st = p.IntermediateStops.FirstOrDefault(s => s.IsEnabled && Math.Abs(s.PositionMm - mm) < 1);
        if (st != null) return "fermo scomparente " + st.Id;
        var sec = p.Sections.FirstOrDefault(s => Math.Abs(s.EndMm - mm) < 1);
        return sec != null ? "fine modulo " + sec.Id : $"{mm:0} mm";
    }
}
