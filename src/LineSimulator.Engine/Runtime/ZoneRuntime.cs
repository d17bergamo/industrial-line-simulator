using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Engine.Runtime;

public enum InverterMode
{
    Stop,
    Run,
    Position,
    Align,
    /// <summary>Ritorno indietro del pacco contro il fermo scomparente (intestatura di coda).</summary>
    Reverse
}

/// <summary>
/// Zona inverter: stato del comando e velocità effettiva dell'azionamento.
/// La velocità segue il riferimento con le rampe configurate (accelerazione/decelerazione costanti).
/// </summary>
public sealed class ZoneRuntime
{
    public ZoneRuntime(ZoneInfo info)
    {
        Info = info;
        // Stato iniziale (utente, 2026-11-22, B14): a linea vuota tutte le vie sono già vuote e ferme; si accendono solo quando
        // serve (consenso d'ingresso, fotocellula di ripartenza a monte, comando della stazione). Le zone senza arresto
        // automatico restano in marcia, scelta dell'utente.
        EmptyTravelM = info.LengthM;
        if (info.Inverter.AutoStopWhenEmpty)
        {
            Mode = InverterMode.Stop;
            AutoStopped = true;
            Reason = "Linea vuota: ferma in attesa del pacco";
        }
    }

    public ZoneInfo Info { get; }
    public InverterConfig Config => Info.Inverter;
    public string Id => Info.Id;
    public bool IsSinglePackage => Info.Kind != ZoneKind.Transport;

    public InverterMode Mode { get; internal set; } = InverterMode.Run;
    public SpeedLevel Level { get; internal set; } = SpeedLevel.High;
    public double ReferenceSpeed { get; internal set; }
    public double ActualSpeed { get; internal set; }
    public string Reason { get; internal set; } = string.Empty;
    /// <summary>Velocità comandata dalla stazione in modalità posizionamento.</summary>
    internal double PositionCommand;

    /// <summary>Pacco che ha prenotato la zona (solo zone a pacco singolo).</summary>
    public PackageRuntime? ReservedBy { get; internal set; }
    public bool Occupied { get; internal set; }
    public bool BlockedByAccumulation { get; internal set; }
    public bool Accepting { get; internal set; }
    /// <summary>Ferma per arresto automatico (nessun pacco in zona, nella sezione precedente o in arrivo).</summary>
    public bool AutoStopped { get; internal set; }
    /// <summary>
    /// Corsa compiuta dai rulli della zona senza che le sue fotocellule (o quella di ripartenza a monte) rilevino pacchi.
    /// Quando raggiunge la lunghezza della zona, la zona è vuota e l'inverter può spegnersi.
    /// </summary>
    public double EmptyTravelM { get; internal set; }
    /// <summary>Fotocellule poste nella zona (rilevano i pacchi presenti).</summary>
    public IReadOnlyList<PhotocellRuntime> OwnPhotocells { get; internal set; } = Array.Empty<PhotocellRuntime>();
    /// <summary>Fotocellula a monte della zona che ne comanda la ripartenza all'arrivo di un pacco (null per la prima zona).</summary>
    public PhotocellRuntime? RestartPhotocell { get; internal set; }
    internal bool UndetectedWarned;
    /// <summary>Zona vuota secondo le fotocellule: corsa a vuoto ≥ lunghezza, nessuna fotocellula della zona o di ripartenza occupata.</summary>
    public bool SensorsEmpty => EmptyTravelM >= Info.LengthM - 1e-9 && !OwnPhotocells.Any(p => p.Output) && !(RestartPhotocell?.Output ?? false);

    /// <summary>Zona dello stopper con fermi scomparenti: può contenere più pacchi, uno per fermo.</summary>
    internal bool MultiStop;
    internal bool MultiCanAcceptNew;
    internal readonly List<PackageRuntime> Assigned = new();
    /// <summary>Ingresso bloccato (es. rigetto in corso) tranne che per il pacco indicato.</summary>
    internal bool EntryHeld;
    internal PackageRuntime? EntryHeldFor;
    /// <summary>Ingresso negato a un pacco specifico (es. pacco da rigettare finché la zona non è libera).</summary>
    internal PackageRuntime? EntryBlockedFor;

    public bool Running => Mode != InverterMode.Stop;
    public bool IsMoving => ActualSpeed > 1e-4;
    public IReadOnlyList<PackageRuntime> AssignedPackages => Assigned;

    // Statistiche
    public double RunningTimeS { get; internal set; }
    public double HighSpeedTimeS { get; internal set; }
    public double LowSpeedTimeS { get; internal set; }
    public double OccupiedTimeS { get; internal set; }
    public double BlockedTimeS { get; internal set; }
    public double ReservedTimeS { get; internal set; }
    public int Reservations { get; internal set; }
    public int PackagesEntered { get; internal set; }

    /// <summary>La zona può ricevere il pacco p dalla zona a monte?</summary>
    public bool AcceptsFor(PackageRuntime? p)
    {
        if (!Running && !AutoStopped) return false;
        if (EntryHeld && (p == null || p != EntryHeldFor)) return false;
        if (EntryBlockedFor != null && p == EntryBlockedFor) return false;
        if (MultiStop) return (p != null && Assigned.Contains(p)) || MultiCanAcceptNew;
        return !IsSinglePackage || ReservedBy == null || (p != null && ReservedBy == p);
    }

    /// <summary>Velocità richiesta dal comando della zona, prima del sincronismo con le zone vicine.</summary>
    internal double TargetSpeed(double alignSpeed) => Mode switch
    {
        InverterMode.Stop => 0,
        InverterMode.Run => Level == SpeedLevel.High ? Config.HighSpeed : Config.LowSpeed,
        InverterMode.Position => Math.Max(0, PositionCommand),
        InverterMode.Align => Math.Max(0, alignSpeed),
        _ => 0
    };

    internal void UpdateDrive(double dt, double alignSpeed, double handoverCap = double.PositiveInfinity)
    {
        ReferenceSpeed = TargetSpeed(alignSpeed);
        // Sincronismo: finché un pacco è a cavallo di più zone, tutte seguono la più lenta (nessuno strisciamento)
        if (ReferenceSpeed > handoverCap) ReferenceSpeed = Math.Max(0, handoverCap);
        if (ActualSpeed < ReferenceSpeed)
            ActualSpeed = Math.Min(ReferenceSpeed, ActualSpeed + Config.Acceleration * dt);
        else if (ActualSpeed > ReferenceSpeed)
            ActualSpeed = Math.Max(ReferenceSpeed, ActualSpeed - Config.Deceleration * dt);
    }

    /// <summary>Codice di stato per ciclogramma.</summary>
    public CycleState CycleStateCode
    {
        get
        {
            const double e = 1e-4;
            if (Mode == InverterMode.Reverse) return CycleState.Positioning;
            if (ActualSpeed < e && ReferenceSpeed < e) return CycleState.Stopped;
            if (ReferenceSpeed > ActualSpeed + e) return CycleState.Accelerating;
            if (ReferenceSpeed < ActualSpeed - e) return CycleState.Decelerating;
            return Mode switch
            {
                InverterMode.Position => CycleState.Positioning,
                InverterMode.Align => CycleState.Aligning,
                _ => Level == SpeedLevel.High ? CycleState.High : CycleState.Low
            };
        }
    }
}
