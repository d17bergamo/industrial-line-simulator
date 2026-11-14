using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Banking e intestatura di ogni tipologia di pacco (tabella della schermata Pacchi): scelte possibili, predefinite e
/// quelle usate dalla simulazione.
/// Regole:
/// - double banking solo se due pacchi di quella lunghezza stanno sul trasferitore (con il fermo scomparente o, senza,
///   fermati per posizionamento a <see cref="TransferConfig.DoubleBankingGapMm"/> dal primo, sbalzo ammesso incluso);
/// - intestatura di coda con il fermo scomparente solo se il pacco ci sta oltre il fermo (extra corsa inclusa);
///   senza fermo scomparente solo in single banking: il pacco si ferma per posizionamento con la coda a inizio trasferitore;
/// - predefinita: di coda se il fermo scomparente è all'estremità del trasferitore, altrimenti di testa.
/// </summary>
public static class BankingOptions
{
    private static List<IntermediateStopConfig> Stops(LineProject p) =>
        p.IntermediateStops.Where(s => s.IsEnabled).OrderByDescending(s => s.PositionMm).ToList();

    /// <summary>L'intestatura di coda richiede almeno un fermo scomparente abilitato (impostazione generale della simulazione).</summary>
    public static bool TailHeadingAvailable(LineProject p) => p.IntermediateStops.Any(s => s.IsEnabled);

    /// <summary>Intestatura predefinita della linea: di coda con un fermo scomparente all'estremità del trasferitore.</summary>
    public static StopReference DefaultHeading(LineProject p) =>
        Stops(p).Any(s => s.Placement == DevicePlacement.End) ? StopReference.Tail : StopReference.Head;

    /// <summary>
    /// Margine minimo dal primo pacco durante l'extra corsa dell'intestatura di coda (mm): lo stesso nelle regole e nel motore
    /// (prima 20 mm qui e 50 mm nel motore).
    /// </summary>
    public const double TailOvershootClearanceMm = 50;

    /// <summary>Double banking possibile per il pacco (con almeno un'intestatura).</summary>
    public static bool DoubleBankingAvailable(LineProject p, PackageType t) =>
        HeadingAvailable(p, t, AccumulationMode.DoubleBanking, StopReference.Head) || HeadingAvailable(p, t, AccumulationMode.DoubleBanking, StopReference.Tail);

    /// <summary>Perché il double banking non è possibile per il pacco (per la tabella Pacchi e i messaggi); null se è possibile.</summary>
    public static string? DoubleBankingBlockReason(LineProject p, PackageType t)
    {
        if (DoubleBankingAvailable(p, t)) return null;
        double split = TransferSplitRule.SplitFor(p, t);
        if (split <= 0)
            return "la via del trasferitore non si può dividere in due inverter con il primo pacco tutto a valle e il secondo tutto a monte: serve un fermo scomparente o un trasferitore più lungo";
        if (p.Stopper.PositionMm - t.LengthMm < split - 1e-6)
            return $"il pacco da {t.LengthMm:0} mm è più lungo della parte di valle della via del trasferitore ({p.Stopper.PositionMm - split:0} mm dal punto di divisione al fermo finale)";
        return $"due pacchi da {t.LengthMm:0} mm non stanno sul trasferitore, il primo a valle e il secondo a monte del punto di divisione ({split:0} mm)";
    }

    /// <summary>L'intestatura <paramref name="h"/> è possibile per il pacco con il banking <paramref name="b"/>.</summary>
    public static bool HeadingAvailable(LineProject p, PackageType t, AccumulationMode b, StopReference h)
    {
        double len = t.LengthMm, start = p.Transfer.PositionMm, stopper = p.Stopper.PositionMm;
        var stops = Stops(p);
        if (b == AccumulationMode.SingleBanking)
        {
            if (h == StopReference.Head) return true;
            if (stops.Count == 0) return start + len <= stopper + 1e-6;               // coda a inizio trasferitore, per posizionamento
            return stops.Any(s => s.PositionMm >= start - 1e-6 && s.PositionMm + len + s.TailOvershootMm <= stopper + 1e-6);
        }
        // Double banking: il primo di testa sul fermo finale, il secondo dietro. La via del trasferitore è su due inverter:
        // il primo pacco deve stare tutto a valle del punto di divisione e il secondo tutto a monte (nessuno strisciamento)
        double firstTail = stopper - len;
        double split = TransferSplitRule.SplitFor(p, t);
        if (split <= 0 || firstTail < split - 1e-6) return false;
        if (stops.Count == 0)
        {
            if (h == StopReference.Tail) return false;                                  // il secondo si ferma a distanza dal primo
            double head2 = Math.Min(firstTail - p.Transfer.DoubleBankingGapMm, split);  // al più tardi sul punto di divisione
            return head2 - len >= start - p.Transfer.EffectiveMaxOverhangMm - 1e-6;
        }
        double safety = p.Simulation.MinSafetyDistanceMm;
        foreach (var s in stops)
        {
            double head2 = h == StopReference.Head ? s.PositionMm : s.PositionMm + len;
            double reach = h == StopReference.Head ? head2 : head2 + s.TailOvershootMm;
            if (head2 - len < start - 1e-6) continue;
            if (reach > split + 1e-6) continue;
            if (head2 > firstTail - safety + 1e-6) continue;
            if (reach > firstTail - Math.Min(safety, TailOvershootClearanceMm) + 1e-6) continue;
            return true;
        }
        return false;
    }

    /// <summary>Banking del pacco come scelto (o, se mai scelto, come le impostazioni della simulazione), senza verifiche.</summary>
    public static AccumulationMode ChosenBanking(LineProject p, PackageType t) => t.Banking ?? p.Simulation.Banking;

    /// <summary>
    /// Intestatura del pacco come scelta (o predefinita), senza verifiche. L'impostazione generale "coda" dei progetti
    /// salvati vale solo con un fermo scomparente (come prima della tabella).
    /// </summary>
    public static StopReference ChosenHeading(LineProject p, PackageType t) =>
        t.Heading ?? (p.Simulation.Heading == StopReference.Tail && TailHeadingAvailable(p) ? StopReference.Tail : DefaultHeading(p));

    /// <summary>Banking del pacco riportato a una scelta possibile.</summary>
    public static AccumulationMode Banking(LineProject p, PackageType t)
    {
        var b = ChosenBanking(p, t);
        return b == AccumulationMode.DoubleBanking && !DoubleBankingAvailable(p, t) ? AccumulationMode.SingleBanking : b;
    }

    /// <summary>Intestatura del pacco riportata a una scelta possibile (con il banking possibile).</summary>
    public static StopReference Heading(LineProject p, PackageType t)
    {
        var b = Banking(p, t);
        var h = ChosenHeading(p, t);
        if (HeadingAvailable(p, t, b, h)) return h;
        var other = h == StopReference.Head ? StopReference.Tail : StopReference.Head;
        return HeadingAvailable(p, t, b, other) ? other : StopReference.Head;
    }

    /// <summary>Tipologie della sequenza simulata (vuota = la prima tipologia).</summary>
    public static List<PackageType> SimulatedTypes(LineProject p)
    {
        var list = p.Infeed.TypeSequence.Select(id => p.PackageTypes.FirstOrDefault(t => t.Id == id)).Where(t => t != null).Select(t => t!).Distinct().ToList();
        if (list.Count == 0 && p.PackageTypes.Count > 0) list.Add(p.PackageTypes[0]);
        return list;
    }

    /// <summary>
    /// Banking e intestatura per la simulazione: quelli scelti per la tipologia simulata (con più tipologie, double banking
    /// solo se scelto per tutte, intestatura della prima). Senza tipologie: le impostazioni della simulazione.
    /// La simulazione poi verifica pacco per pacco se ci sta (altrimenti di testa sul fermo finale).
    /// </summary>
    public static (AccumulationMode Banking, StopReference Heading) ForSimulation(LineProject p)
    {
        var types = SimulatedTypes(p);
        if (types.Count == 0) return (p.Simulation.Banking, p.Simulation.Heading);
        // Double banking attivo se è scelto per almeno una tipologia: la simulazione abbina solo i pacchi che lo hanno e ci
        // stanno (prima bastava una tipologia troppo lunga, riportata a single, per spegnerlo su tutta la simulazione)
        var banking = types.Any(t => ChosenBanking(p, t) == AccumulationMode.DoubleBanking) ? AccumulationMode.DoubleBanking : AccumulationMode.SingleBanking;
        return (banking, ChosenHeading(p, types[0]));
    }

    /// <summary>Imposta lo stesso banking a tutte le tipologie (proposte dell'ottimizzatore).</summary>
    public static void SetBankingAll(LineProject p, AccumulationMode b)
    {
        p.Simulation.Banking = b;
        foreach (var t in p.PackageTypes) t.Banking = b;
    }

    /// <summary>
    /// Riporta l'intestatura generale a "testa" se la coda non è disponibile e le scelte dei pacchi a quelle possibili
    /// (cambiato il layout); restituisce true se ha cambiato il progetto.
    /// </summary>
    public static bool Normalize(LineProject p) => NormalizeChanges(p).Count > 0;

    /// <summary>Come <see cref="Normalize"/>, con la descrizione di ogni scelta riportata a una possibile.</summary>
    public static List<string> NormalizeChanges(LineProject p)
    {
        var done = new List<string>();
        if (p.Simulation.Heading == StopReference.Tail && !TailHeadingAvailable(p))
        {
            p.Simulation.Heading = StopReference.Head;
            done.Add("Intestatura di coda non disponibile: la linea non ha un fermo scomparente abilitato. Impostata l'intestatura di testa.");
        }
        foreach (var t in p.PackageTypes)
        {
            if (t.Banking == AccumulationMode.DoubleBanking && DoubleBankingBlockReason(p, t) is { } reason)
            {
                t.Banking = AccumulationMode.SingleBanking;
                done.Add($"Pacco {t.Id}: double banking non possibile ({reason}). Impostato il single banking.");
            }
            if (t.Heading is { } h && !HeadingAvailable(p, t, Banking(p, t), h))
            {
                t.Heading = Heading(p, t);
                done.Add($"Pacco {t.Id}: intestatura di {(h == StopReference.Tail ? "coda" : "testa")} non possibile su questa linea. Impostata l'intestatura di {(t.Heading == StopReference.Tail ? "coda" : "testa")}.");
            }
        }
        return done;
    }
}
