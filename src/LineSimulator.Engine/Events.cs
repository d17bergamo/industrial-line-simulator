using System.Globalization;

namespace LineSimulator.Engine;

public enum EventSeverity
{
    Trace = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public enum SimEventType
{
    ConfigurationError,
    PackageEntered,
    InfeedWaiting,
    PackageExited,
    PhotocellChanged,
    InverterCommand,
    AccumulationStop,
    PositioningStarted,
    PositioningCompleted,
    PositioningError,
    StrapperPhase,
    StrappingCompleted,
    StrappingPlanError,
    StopperContact,
    ExcessiveImpact,
    AlignmentCompleted,
    TransferPhase,
    Collision,
    SafetyDistanceViolation,
    TransferInterference,
    ZoneOverfilled,
    Slip,
    Deadlock,
    TimeLimit,
    Completed,
    PackageIdentified,
    PhotocellArrival,
    PhotocellCleared,
    ZoneCleared,
    AutoStop,
    AutoStart,
    StrapperProcess,
    StrapperFault,
    BundleClampPhase,
    CompactorPhase,
    Interlock,
    DeviceAlarm,
    RejectorPhase,
    PackageRejected,
    StopPhase,
    WeighingPhase,
    WeightAcquired,
    TransferWaiting
}

/// <summary>Evento di simulazione con marca temporale (registro per analisi e step-by-step).</summary>
public sealed record SimEvent(
    long Sequence,
    double TimeS,
    SimEventType Type,
    EventSeverity Severity,
    string Message,
    string? ComponentId = null,
    string? PackageId = null)
{
    public override string ToString() =>
        $"{TimeS.ToString("0.000", CultureInfo.InvariantCulture)} s [{Severity}] {Type}: {Message}";
}
