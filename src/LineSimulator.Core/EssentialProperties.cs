namespace LineSimulator.Core;

/// <summary>
/// Proprietà "essenziali" mostrate subito nella scheda di un componente; tutte le altre vanno in "Avanzate" (chiuso).
/// Un tipo non elencato mostra tutto. La spunta "personalizza" (OverrideGroup) è sempre essenziale.
/// </summary>
public static class EssentialProperties
{
    private static readonly Dictionary<string, string[]> Map = new()
    {
        ["RollerSection"] = new[] { "Name", "StartMm", "LengthMm", "RollerCount", "IsEnabled" },
        ["RollerGroup"] = new[] { "RollerTopElevationMm", "WidthMm", "RollerPitchMm", "MaxSpeed", "HighSpeed", "LowSpeed", "Acceleration" },
        ["PhotocellConfig"] = new[] { "Name", "PositionMm", "Purpose", "Action", "IsEnabled" },
        ["PhotocellGroup"] = new[] { "Type", "ResponseTimeMs" },
        ["EncoderConfig"] = new[] { "IsEnabled", "PositionMm" },
        ["BundleClampConfig"] = new[] { "IsEnabled", "CloseTimeS", "OpenTimeS" },
        ["SideCompactorConfig"] = new[] { "IsEnabled", "CloseTimeS", "OpenTimeS" },
        ["RejectorConfig"] = new[] { "IsEnabled", "PositionMm", "Side", "EjectTimeS" },
        ["IntermediateStopConfig"] = new[] { "Name", "Placement", "IsEnabled" },
        ["WeighingScaleConfig"] = new[] { "Name", "Placement", "AcquisitionTimeS" },
        ["ScaleGroup"] = new[] { "AcquisitionTimeS" },
        ["StrapperConfig"] = new[] { "Name", "PositionMm", "Side", "MachineType", "StrappingTimeS", "IsEnabled" },
        ["StrapperGroup"] = new[] { "StrappingTimeS", "ArmingTimeS" },
        ["StopperConfig"] = new[] { "PositionMm", "MaxImpactSpeed" },
        ["TransferConfig"] = new[] { "LengthMm", "WidthMm", "LiftTimeS", "TranslationSpeed", "DepositTimeS", "FullCycleTimeS" },
        ["StrappingStationConfig"] = new[] { "IsEnabled", "Strategy" },
        ["InfeedConfig"] = new[] { "PackageCount", "TypeSequence", "NewPackageDelayS" },
    };

    /// <summary>Tipi con una scheda breve e i loro campi essenziali (per i test).</summary>
    public static IReadOnlyDictionary<string, string[]> All => Map;

    /// <summary>True se la proprietà del tipo indicato va in "Avanzate".</summary>
    public static bool IsAdvanced(string typeName, string propertyName)
    {
        if (propertyName == "OverrideGroup") return false;
        return Map.TryGetValue(typeName, out var list) && !list.Contains(propertyName);
    }
}
