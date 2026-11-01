using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Engine.Runtime;

public enum InverterMode
{
    Stop,
    Run,
    Position,
    Align
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

    public bool Running => Mode != InverterMode.Stop;
    public bool IsMoving => ActualSpeed > 1e-4;

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
        => Running && (!IsSinglePackage || ReservedBy == null || (p != null && ReservedBy == p));

    internal void UpdateDrive(double dt, double alignSpeed)
    {
        ReferenceSpeed = Mode switch
        {
            InverterMode.Stop => 0,
            InverterMode.Run => Level == SpeedLevel.High ? Config.HighSpeed : Config.LowSpeed,
            InverterMode.Position => Math.Max(0, PositionCommand),
            InverterMode.Align => Math.Max(0, alignSpeed),
            _ => 0
        };
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
