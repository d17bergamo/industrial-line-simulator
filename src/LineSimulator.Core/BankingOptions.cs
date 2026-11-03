using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>Scelte di banking e intestatura disponibili per la linea (sezione Simulazioni).</summary>
public static class BankingOptions
{
    /// <summary>L'intestatura di coda richiede almeno un fermo intermedio scomparente abilitato.</summary>
    public static bool TailHeadingAvailable(LineProject p) => p.IntermediateStops.Any(s => s.IsEnabled);

    /// <summary>Riporta l'intestatura a "testa" se la coda non è disponibile; restituisce true se ha cambiato il progetto.</summary>
    public static bool Normalize(LineProject p)
    {
        if (p.Simulation.Heading != StopReference.Tail || TailHeadingAvailable(p)) return false;
        p.Simulation.Heading = StopReference.Head;
        return true;
    }
}
