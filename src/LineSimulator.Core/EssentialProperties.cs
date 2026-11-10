namespace LineSimulator.Core;

/// <summary>
/// Proprietà "essenziali" mostrate subito nella scheda di un componente; tutte le altre vanno in "Avanzate" (chiuso).
/// Un tipo non elencato mostra tutto. La spunta "personalizza" (OverrideGroup) è sempre essenziale.
/// </summary>
public static class EssentialProperties
{
    private static readonly Dictionary<string, string[]> Map = new()
    {
        ["RollerSection"] = new[] { "LengthMm", "RollerCount", "RollerDiameterMm" },
        ["RollerGroup"] = new[] { "RollerTopElevationMm", "WidthMm", "RollerDiameterMm", "MaxSpeed", "HighSpeed", "LowSpeed", "Acceleration" },
        ["PhotocellConfig"] = new[] { "Name", "PositionMm", "Purpose", "IsEnabled" },
        ["PhotocellGroup"] = new[] { "Type", "ResponseTimeMs" },
        ["EncoderConfig"] = new[] { "IsEnabled", "PositionMm" },
        ["BundleClampConfig"] = new[] { "IsEnabled", "CloseTimeS", "OpenTimeS" },
        ["SideCompactorConfig"] = new[] { "IsEnabled", "CloseTimeS", "OpenTimeS" },
        ["RejectorConfig"] = new[] { "IsEnabled", "PositionMm", "Side", "EjectTimeS" },
        ["IntermediateStopConfig"] = new[] { "Name", "Placement", "IsEnabled" },
        ["WeighingScaleConfig"] = new[] { "Name", "Mounting", "Placement", "AcquisitionTimeS" },
        ["ScaleGroup"] = new[] { "Mounting", "AcquisitionTimeS" },
        ["StrapperConfig"] = new[] { "Name", "PositionMm", "Side", "MachineType", "StrappingTimeS", "IsEnabled" },
        ["StrapperGroup"] = new[] { "StrappingTimeS", "ArmingTimeS" },
        ["StopperConfig"] = new[] { "PositionMm" },
        ["AccumulationTransferConfig"] = new[] { "WidthMm", "StepGapMm", "StepSpeed" },
        ["TransferConfig"] = new[] { "LengthMm", "WidthMm", "LiftTimeS", "TranslationSpeed", "DepositTimeS", "FullCycleTimeS" },
        ["StrappingStationConfig"] = new[] { "IsEnabled", "Strategy" },
        ["InfeedConfig"] = new[] { "PackageCount", "TypeSequence", "NewPackageDelayS" },
    };

    /// <summary>Tipi con una scheda breve e i loro campi essenziali (per i test).</summary>
    public static IReadOnlyDictionary<string, string[]> All => Map;

    /// <summary>Tipi che mostrano SOLO i campi essenziali (niente "Avanzate").</summary>
    private static readonly HashSet<string> OnlyEssential = new() { "RollerSection" };

    /// <summary>Campi mai mostrati: l'azione delle fotocellule la decide il modello, l'impatto sul fermo finale non si imposta.</summary>
    private static readonly HashSet<(string, string)> Never = new() { ("PhotocellConfig", "Action"), ("StopperConfig", "MaxImpactSpeed"), ("AccumulationTransferConfig", "IsEnabled") };

    /// <summary>True se la proprietà non va mostrata affatto.</summary>
    public static bool IsHidden(string typeName, string propertyName) =>
        Never.Contains((typeName, propertyName)) ||
        (OnlyEssential.Contains(typeName) && Map.TryGetValue(typeName, out var l) && !l.Contains(propertyName));

    /// <summary>True se la proprietà del tipo indicato va in "Avanzate".</summary>
    public static bool IsAdvanced(string typeName, string propertyName)
    {
        if (propertyName == "OverrideGroup") return false;
        return Map.TryGetValue(typeName, out var list) && !list.Contains(propertyName);
    }
}
