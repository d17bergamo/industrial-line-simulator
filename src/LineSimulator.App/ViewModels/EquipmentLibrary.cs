namespace LineSimulator.App.ViewModels;

/// <summary>Voce della libreria macchine: dimensioni indicative usate solo per l'anteprima di trascinamento.</summary>
public sealed record LibraryEntry(string Key, string Category, string Name, string Description, string Icon,
    double LengthM, double WidthM, double HeightM)
{
    public override string ToString() => Name;
}

/// <summary>Elenco delle macchine disponibili per comporre la linea. Le dimensioni reali si impostano dopo l'inserimento.</summary>
public static class EquipmentLibrary
{
    public const string CatRollers = "Vie a rulli";
    public const string CatStrappers = "Reggiatrici";
    public const string CatRejectors = "Rigettatrici";
    public const string CatKnof = "Compattatori Knof Sund";
    public const string CatCompactors = "Compattatori laterali";
    public const string CatOthers = "Altri equipaggiamenti";

    public static IReadOnlyList<LibraryEntry> Entries { get; } = new List<LibraryEntry>
    {
        new("roller", CatRollers, "Modulo via a rulli", "Modulo con inverter proprio, passo e numero rulli parametrici",
            "M2,16 L30,16 M2,22 L30,22 M6,16 L6,22 M12,16 L12,22 M18,16 L18,22 M24,16 L24,22", 6.0, 1.2, 0.8),
        new("knca", CatStrappers, "SUND BIRSTA KNCA 7/800", "Legatrice ad arco (modello semplificato)",
            "M6,24 L6,6 L24,6 L24,24 M3,24 L27,24", 1.2, 2.1, 2.1),
        new("sma", CatStrappers, "SUND BIRSTA SMA", "Reggettatrice con reggia d'acciaio: l'arco scorre sulla colonna (modello semplificato)",
            "M3,26 L27,26 M8,26 L8,6 M8,10 L24,10 L24,20 L8,20", 1.2, 2.1, 2.2),
        new("knof", CatKnof, "SUND BIRSTA KNOF", "Stringifascio: chiude il fascio durante la legatura",
            "M5,26 L5,8 L25,8 L25,26 M5,15 L25,15", 0.3, 1.9, 2.2),
        new("compactor", CatCompactors, "Compattatore laterale", "Due ganasce con apertura e chiusura regolabili",
            "M3,8 L9,8 L9,22 L3,22 Z M27,8 L21,8 L21,22 L27,22 Z M11,15 L19,15", 1.5, 1.4, 1.0),
        new("chains", CatOthers, "Trasferitore a catene", "Trasferitore a catene: preleva i pacchi dalla linea e li porta lateralmente",
            "M3,20 L27,20 M3,14 L27,14 M3,14 L3,20 M27,14 L27,20 M9,14 L9,20 M15,14 L15,20 M21,14 L21,20", 12.0, 1.4, 0.3),
        new("scale", CatOthers, "Bilico di pesatura", "Piattaforma di pesatura lungo la linea",
            "M3,20 L27,20 L27,25 L3,25 Z M6,20 L6,14 L24,14 L24,20", 6.5, 1.2, 0.8),
        new("stop", CatOthers, "Fermo intermedio", "Fermo sollevabile per intestatura intermedia",
            "M3,24 L27,24 M12,24 L12,10 L18,10 L18,24", 0.1, 0.6, 0.25),
        new("photocell", CatOthers, "Fotocellula", "Barriera con trasmettitore e ricevitore",
            "M5,26 L5,6 M25,26 L25,6 M5,12 L25,12", 0.06, 1.8, 1.1)
    };

    public static LibraryEntry Find(string key) => Entries.First(e => e.Key == key);
}
