using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Fotocellule aggiunte a mano: sono sempre di sola presenza (regola dell'utente, 2026-11-22). Rallentamento, arresto e
/// accumulo li mette il programma (<see cref="PhotocellRules.ApplyProcess"/>) in base ai pacchi configurati.
/// </summary>
public static class PhotocellRoles
{
    public static string Decide(LineProject p, PhotocellConfig pc)
    {
        pc.Purpose = PhotocellPurpose.Generic;
        pc.DetectionMode = PhotocellDetectionMode.Presence;
        pc.Action = PhotocellAction.None;
        if (pc.Name.StartsWith("Nuova", StringComparison.Ordinal)) pc.Name = "Presenza";
        return $"{pc.Id}: presenza (nessuna azione sull'inverter)";
    }
}
