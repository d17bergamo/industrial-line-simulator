using System.Text.Json;
using LineSimulator.Models;

namespace LineSimulator.Infrastructure;

/// <summary>
/// Salvataggio indipendente di layout e tipologie di pacco. Il file di layout contiene equipaggiamenti, coordinate,
/// parametri dimensionali e configurazione delle vie a rulli, ma nessuna tipologia di pacco; il file delle tipologie
/// contiene solo le tipologie.
/// </summary>
public static class SplitFiles
{
    private sealed class PackagesFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<PackageType> PackageTypes { get; set; } = new();
    }

    /// <summary>Copia del progetto senza tipologie di pacco né sequenza d'ingresso.</summary>
    public static LineProject ExtractLayout(LineProject project)
    {
        var layout = ProjectSerializer.Clone(project);
        layout.PackageTypes.Clear();
        layout.Infeed.TypeSequence = new List<string>();
        return layout;
    }

    public static void SaveLayout(LineProject project, string path) => File.WriteAllText(path, ProjectSerializer.Serialize(ExtractLayout(project)));

    /// <summary>Layout letto da file, con le tipologie di pacco (e la sequenza d'ingresso) mantenute da <paramref name="current"/>.</summary>
    public static LineProject LoadLayout(string path, LineProject current)
    {
        var layout = ProjectSerializer.Deserialize(File.ReadAllText(path));
        return WithPackagesOf(layout, current);
    }

    public static LineProject WithPackagesOf(LineProject layout, LineProject packagesSource)
    {
        layout.PackageTypes = ProjectSerializer.Clone(packagesSource).PackageTypes;
        layout.Infeed.TypeSequence = new List<string>(packagesSource.Infeed.TypeSequence);
        return layout;
    }

    public static void SavePackages(IEnumerable<PackageType> types, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(new PackagesFile { PackageTypes = types.ToList() }, ProjectSerializer.Options));

    public static List<PackageType> LoadPackages(string path)
    {
        var f = JsonSerializer.Deserialize<PackagesFile>(File.ReadAllText(path), ProjectSerializer.Options)
                ?? throw new InvalidDataException("File delle tipologie vuoto.");
        if (f.PackageTypes.Count == 0) throw new InvalidDataException("Il file non contiene tipologie di pacco.");
        return f.PackageTypes;
    }
}
