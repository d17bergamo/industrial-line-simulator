using LineSimulator.Models;

namespace LineSimulator.Core;

/// <summary>
/// Fotocellule aggiunte a mano: è il modello a deciderne l'utilità. Sulla via davanti al trasferitore, a monte del fermo finale e
/// se manca ancora, fa da rallentamento; in tutti gli altri casi è una fotocellula di presenza (nessuna azione sull'inverter).
/// </summary>
public static class PhotocellRoles
{
    public static string Decide(LineProject p, PhotocellConfig pc)
    {
        var trfMod = LayoutRules.TransferModule(p);
        bool onTransferModule = trfMod != null && pc.PositionMm >= trfMod.StartMm - 1e-6 && pc.PositionMm < p.Stopper.PositionMm - 1e-6;
        bool slowExists = p.Photocells.Any(o => !ReferenceEquals(o, pc) && o.IsEnabled && o.Action == PhotocellAction.SlowDown
                                               && trfMod != null && o.PositionMm >= trfMod.StartMm - 1e-6 && o.PositionMm <= trfMod.EndMm + 1e-6);
        pc.Purpose = PhotocellPurpose.Generic;
        pc.DetectionMode = PhotocellDetectionMode.Presence;
        if (onTransferModule && !slowExists)
        {
            pc.Action = PhotocellAction.SlowDown;
            if (pc.Name.StartsWith("Nuova", StringComparison.Ordinal)) pc.Name = "Rallentamento";
            return $"{pc.Id}: rallentamento prima del fermo finale (non ce n'era uno)";
        }
        pc.Action = PhotocellAction.None;
        if (pc.Name.StartsWith("Nuova", StringComparison.Ordinal)) pc.Name = "Presenza";
        return $"{pc.Id}: presenza (nessuna azione sull'inverter)";
    }
}
