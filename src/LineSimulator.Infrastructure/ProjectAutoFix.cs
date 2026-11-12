using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Models;

namespace LineSimulator.Infrastructure;

/// <summary>Correzione automatica con la sua origine (per non ripetere nella scheda Controllo ciò che è già elencato).</summary>
public sealed record AutoFixChange(string Source, string Message);

/// <summary>Esito dei controlli automatici: correzioni (applicate o da applicare) ed errori che impediscono la simulazione.</summary>
public sealed class AutoFixReport
{
    public List<AutoFixChange> Changes { get; } = new();
    public List<string> Errors { get; } = new();
    public bool Changed => Changes.Count > 0;
    public bool HasErrors => Errors.Count > 0;
    public IEnumerable<string> Messages => Changes.Select(c => c.Message);
}

/// <summary>
/// Momento unico dei controlli: tutte le regole automatiche del progetto in un solo passaggio e in un ordine fisso
/// (regole di layout e dell'area di legatura, controllo equipaggiamenti, fotocellule, preparazione del ciclo).
/// Lo usano l'avvio della simulazione, "Sistema automaticamente" nella scheda Controllo, l'ottimizzatore della produttività,
/// la vista dall'alto e l'apertura dei progetti salvati: l'elenco della scheda Controllo è esattamente ciò che verrà
/// corretto all'avvio della simulazione.
/// </summary>
public static class ProjectAutoFix
{
    public const string SourceLayout = "Layout", SourceCheck = "Controllo", SourcePhotocells = "Fotocellule", SourcePreparation = "Preparazione";

    /// <summary>Applica tutte le correzioni al progetto e restituisce l'elenco.</summary>
    public static AutoFixReport Run(LineProject p)
    {
        var r = new AutoFixReport();
        void Add(string src, IEnumerable<string> msgs)
        {
            foreach (var m in msgs)
                if (!string.IsNullOrWhiteSpace(m) && !r.Changes.Any(c => c.Message == m)) r.Changes.Add(new AutoFixChange(src, m));
        }
        Add(SourceLayout, LayoutRules.Enforce(p));
        Add(SourceCheck, EquipmentCheck.Run(p, fix: true).Where(i => i.Fixed).Select(i => i.Message));
        var outside = p.Sections.Count == 0 ? new List<string>()
            : p.Photocells.Where(x => x.PositionMm < p.Sections.Min(s => s.StartMm) - 1e-6 || x.PositionMm > p.Sections.Max(s => s.EndMm) + 1e-6).Select(x => x.Id).ToList();
        if (ProjectNormalizer.ClampPhotocellsToRollers(p))
            Add(SourcePhotocells, new[] { $"Fotocellule riportate dentro le vie a rulli: {string.Join(", ", outside)}" });
        Add(SourcePhotocells, PhotocellRules.BeforeSimulation(p));
        var prep = SimulationPreparation.Prepare(p);
        Add(SourcePreparation, prep.Changes);
        r.Errors.AddRange(prep.Errors);
        return r;
    }

    /// <summary>Le stesse correzioni su una copia: cosa cambierebbe, senza toccare il progetto.</summary>
    public static AutoFixReport Preview(LineProject p) => Run(ProjectSerializer.Clone(p));
}
