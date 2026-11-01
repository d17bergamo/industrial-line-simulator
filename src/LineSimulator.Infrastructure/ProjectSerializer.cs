using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Infrastructure;

/// <summary>Serializzazione JSON dei progetti con versionamento e migrazione dello schema.</summary>
public static class ProjectSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            AllowOutOfOrderMetadataProperties = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static string Serialize(LineProject project)
    {
        project.SchemaVersion = LineProject.CurrentSchemaVersion;
        return JsonSerializer.Serialize(project, Options);
    }

    public static LineProject Deserialize(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) as JsonObject ?? throw new InvalidDataException("Il file non contiene un oggetto JSON di progetto.");

        int version = node["schemaVersion"]?.GetValue<int>() ?? 1;
        if (version > LineProject.CurrentSchemaVersion)
            throw new InvalidDataException($"Il progetto usa lo schema {version}, più recente di quello supportato ({LineProject.CurrentSchemaVersion}). Aggiornare il programma.");
        Migrate(node, version);

        var p = node.Deserialize<LineProject>(Options) ?? throw new InvalidDataException("Progetto vuoto.");
        // v1 → v2: la fotocellula di ingresso è obbligatoria; i nuovi dispositivi restano disabilitati.
        if (version < 2) ProjectNormalizer.EnsureEntryPhotocell(p);
        p.SchemaVersion = LineProject.CurrentSchemaVersion;
        return p;
    }

    /// <summary>Punto di estensione: trasformazioni successive dello schema (v1 → v2 → ...).</summary>
    private static void Migrate(JsonObject node, int fromVersion)
    {
        int v = fromVersion;
        while (v < LineProject.CurrentSchemaVersion)
        {
            switch (v)
            {
                // case 1: MigrateV1ToV2(node); break;
                default: break;
            }
            v++;
        }
        node["schemaVersion"] = LineProject.CurrentSchemaVersion;
    }

    /// <summary>Copia profonda (via JSON) con nuovo identificativo: "duplica progetto".</summary>
    public static LineProject Duplicate(LineProject source, string? newName = null)
    {
        var copy = Deserialize(Serialize(source));
        copy.ProjectId = Guid.NewGuid().ToString("N");
        copy.Name = newName ?? source.Name + " (copia)";
        return copy;
    }

    public static LineProject Clone(LineProject source) => Deserialize(Serialize(source));
}

/// <summary>Accesso ai file di progetto e salvataggio automatico.</summary>
public sealed class ProjectRepository
{
    public ProjectRepository(string? autosaveDirectory = null)
    {
        AutosaveDirectory = autosaveDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndustrialLineSimulator", "Autosave");
    }

    public string AutosaveDirectory { get; }

    public LineProject Load(string path) => ProjectSerializer.Deserialize(File.ReadAllText(path));

    public void Save(LineProject project, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Scrittura atomica: file temporaneo + sostituzione
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ProjectSerializer.Serialize(project));
        File.Move(tmp, path, overwrite: true);
    }

    public string Autosave(LineProject project)
    {
        Directory.CreateDirectory(AutosaveDirectory);
        string path = Path.Combine(AutosaveDirectory, $"autosave_{project.ProjectId}.json");
        Save(project, path);
        return path;
    }

    public string? LatestAutosave()
    {
        if (!Directory.Exists(AutosaveDirectory)) return null;
        return new DirectoryInfo(AutosaveDirectory).GetFiles("autosave_*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }
}
