using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Modifica delle legature di una tipologia di pacco. Regola: le posizioni (misurate dalla testa) stanno su passi di 100 mm,
/// quindi anche le distanze tra legature sono multipli di 100 mm.
/// </summary>
public static class StrapEditing
{
    public const double StepMm = 100;

    public static double Snap(double mm) => Math.Round(mm / StepMm, MidpointRounding.AwayFromZero) * StepMm;

    /// <summary>Porta tutte le posizioni su multipli di 100 mm, dentro il pacco, senza doppioni e in ordine.</summary>
    public static void SnapAll(PackageType p)
    {
        p.StrapPositionsMm = p.StrapPositionsMm.Select(Snap).Where(v => v > 0 && v < p.LengthMm).Distinct().OrderBy(v => v).ToList();
    }

    /// <summary>
    /// Cambiata la lunghezza del pacco, le legature si spostano in proporzione (a passi di 100 mm) senza cambiarne il numero:
    /// se due legature cadrebbero sullo stesso punto, la successiva scorre di un passo.
    /// </summary>
    public static void ScaleToLength(PackageType p, double oldLengthMm)
    {
        if (oldLengthMm <= 0 || p.LengthMm <= 0 || p.StrapPositionsMm.Count == 0) return;
        double ratio = p.LengthMm / oldLengthMm;
        var pos = p.StrapPositionsMm.OrderBy(v => v).Select(v => Snap(v * ratio)).ToList();
        int n = pos.Count;
        for (int i = 0; i < n; i++)
        {
            double min = i == 0 ? StepMm : pos[i - 1] + StepMm;
            if (pos[i] < min) pos[i] = min;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double max = i == n - 1 ? Math.Max(StepMm, p.LengthMm - StepMm) : pos[i + 1] - StepMm;
            if (pos[i] > max) pos[i] = max;
        }
        p.StrapPositionsMm = pos;
    }

    /// <summary>
    /// Aggiunge una legatura nel punto più libero (al centro del tratto più lungo), su multipli di 100 mm e nel rispetto
    /// delle distanze minime. Restituisce false se non c'è posto.
    /// </summary>
    public static bool TryAdd(PackageType p, out double added)
    {
        added = 0;
        var existing = p.StrapPositionsMm.OrderBy(v => v).ToList();
        double best = -1, bestClearance = -1;
        for (double x = StepMm; x < p.LengthMm; x += StepMm)
        {
            if (x < p.MinStrapDistanceFromEndsMm || x > p.LengthMm - p.MinStrapDistanceFromEndsMm) continue;
            if (existing.Any(e => Math.Abs(e - x) < p.MinDistanceBetweenStrapsMm || e == x)) continue;
            double clearance = Math.Min(x, p.LengthMm - x);
            foreach (var e in existing) clearance = Math.Min(clearance, Math.Abs(e - x));
            if (clearance > bestClearance) { bestClearance = clearance; best = x; }
        }
        if (best < 0) return false;
        existing.Add(best);
        p.StrapPositionsMm = existing.OrderBy(v => v).ToList();
        added = best;
        return true;
    }

    /// <summary>
    /// Mette <paramref name="count"/> legature in modo simmetrico rispetto al centro del pacco: la distanza dalle estremità
    /// (quella attuale della prima legatura, almeno il minimo) resta, le altre sono equidistanti, a passi di 100 mm.
    /// Restituisce false (senza modificare nulla) se le distanze minime non sono rispettate.
    /// </summary>
    public static bool TrySetSymmetric(PackageType p, int count)
    {
        double len = p.LengthMm;
        if (count < 0 || len <= 0) return false;
        if (count == 0) { p.StrapPositionsMm = new List<double>(); return true; }
        var cur = p.StrapPositionsMm.OrderBy(v => v).ToList();
        double end = cur.Count > 0 ? Math.Min(cur[0], len - cur[^1]) : p.MinStrapDistanceFromEndsMm;
        end = Snap(Math.Max(end, p.MinStrapDistanceFromEndsMm));
        var pos = new double[count];
        if (count == 1) pos[0] = Snap(len / 2);
        else
        {
            double gap = (len - 2 * end) / (count - 1);
            for (int i = 0; i < count / 2; i++)
            {
                pos[i] = Snap(end + gap * i);
                pos[count - 1 - i] = len - pos[i];
            }
            if (count % 2 == 1) pos[count / 2] = Snap(len / 2);
        }
        for (int i = 0; i < count; i++)
        {
            if (pos[i] < p.MinStrapDistanceFromEndsMm || pos[i] > len - p.MinStrapDistanceFromEndsMm) return false;
            if (i > 0 && pos[i] - pos[i - 1] < Math.Max(p.MinDistanceBetweenStrapsMm, StepMm)) return false;
        }
        p.StrapPositionsMm = pos.ToList();
        return true;
    }

    /// <summary>Aggiunge una legatura rimettendo tutte in posizione simmetrica.</summary>
    public static bool AddSymmetric(PackageType p) => TrySetSymmetric(p, p.StrapPositionsMm.Count + 1);

    /// <summary>Toglie una legatura rimettendo le rimaste in posizione simmetrica; false se non ce ne sono.</summary>
    public static bool RemoveSymmetric(PackageType p) => p.StrapPositionsMm.Count > 0 && TrySetSymmetric(p, p.StrapPositionsMm.Count - 1);

    /// <summary>Toglie la legatura all'indice indicato (nell'elenco ordinato); false se l'indice non esiste.</summary>
    public static bool Remove(PackageType p, int index)
    {
        var sorted = p.StrapPositionsMm.OrderBy(v => v).ToList();
        if (index < 0 || index >= sorted.Count) return false;
        sorted.RemoveAt(index);
        p.StrapPositionsMm = sorted;
        return true;
    }
}
