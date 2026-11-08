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

    public static LineProject Deserialize(string json) => Deserialize(json, normalize: true);

    /// <summary>
    /// Legge un progetto. Con <paramref name="normalize"/> = false si applica solo la migrazione dello schema
    /// (serve a confrontare il file con la versione aggiornata, vedi <see cref="ProjectUpgrade"/>).
    /// </summary>
    public static LineProject Deserialize(string json, bool normalize)
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
        MigrateBankingSettings(node, p);
        if (normalize)
        {
            // v1 → v2: la fotocellula di ingresso è obbligatoria; i nuovi dispositivi restano disabilitati.
            if (version < 2) ProjectNormalizer.EnsureEntryPhotocell(p);
            // Sensori di processo obbligatori (arresto davanti al trasferitore, riferimento encoder di legatura)
            ProjectNormalizer.EnsureProcessSensors(p);
        }
        p.SchemaVersion = LineProject.CurrentSchemaVersion;
        return p;
    }

    /// <summary>
    /// Progetti salvati prima che banking e intestatura passassero alle impostazioni della simulazione:
    /// "pacchi per ciclo" ≥ 2 con un fermo scomparente → double banking; fermo scomparente con riferimento di coda → intestatura di coda.
    /// </summary>
    private static void MigrateBankingSettings(JsonObject node, LineProject p)
    {
        var sim = Get(node, "simulation") as JsonObject;
        var stops = p.IntermediateStops.Where(s => s.IsEnabled).ToList();
        if (stops.Count == 0) return;
        if (sim == null || Get(sim, "banking") == null)
            p.Simulation.Banking = p.Transfer.BatchSize >= 2 ? AccumulationMode.DoubleBanking : AccumulationMode.SingleBanking;
        if (sim == null || Get(sim, "heading") == null)
            p.Simulation.Heading = stops.Any(s => s.Reference == StopReference.Tail) ? StopReference.Tail : StopReference.Head;
    }

    /// <summary>Punto di estensione: trasformazioni successive dello schema (v1 → v2 → ...).</summary>
    private static void Migrate(JsonObject node, int fromVersion)
    {
        int v = fromVersion;
        while (v < LineProject.CurrentSchemaVersion)
        {
            switch (v)
            {
                // v1 → v2: solo proprietà aggiunte (la fotocellula di ingresso viene aggiunta dopo la deserializzazione)
                case 2: MigrateV2ToV3(node); break;
                default: break;
            }
            v++;
        }
        node["schemaVersion"] = LineProject.CurrentSchemaVersion;
    }

    /// <summary>
    /// v2 → v3: inverter e sezioni diventano "moduli di via a rulli" (un modulo = un inverter).
    /// Le sezioni dello stesso inverter vengono fuse in un modulo con l'ID dell'inverter, così fotocellule,
    /// stazione di legatura, regole e condizioni continuano a riferirsi allo stesso identificativo.
    /// </summary>
    private static void MigrateV2ToV3(JsonObject node)
    {
        var invs = (Get(node, "inverters") as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var secs = (Get(node, "sections") as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var invById = new Dictionary<string, JsonObject>();
        foreach (var inv in invs)
        {
            string id = Str(Get(inv, "id"));
            if (id.Length > 0) invById.TryAdd(id, inv);
        }
        var groups = new List<(string Key, JsonObject? Inv, List<JsonObject> Secs)>();
        foreach (var sec in secs)
        {
            string invId = Str(Get(sec, "inverterId"));
            invById.TryGetValue(invId, out var inv);
            string key = inv != null ? "INV:" + invId : "SEC:" + Str(Get(sec, "id")) + ":" + groups.Count;
            int gi = groups.FindIndex(g => g.Key == key);
            if (gi < 0) groups.Add((key, inv, new List<JsonObject> { sec }));
            else groups[gi].Secs.Add(sec);
        }
        var modules = new JsonArray();
        foreach (var g in groups.OrderBy(g => g.Secs.Min(x => Num(Get(x, "startMm"), 0))))
        {
            var ordered = g.Secs.OrderBy(x => Num(Get(x, "startMm"), 0)).ToList();
            var first = ordered[0];
            double start = Num(Get(first, "startMm"), 0);
            double end = ordered.Max(x => Num(Get(x, "startMm"), 0) + Num(Get(x, "lengthMm"), 6000));
            var m = new JsonObject
            {
                ["id"] = g.Inv != null ? Str(Get(g.Inv, "id")) : Str(Get(first, "id")),
                ["name"] = g.Inv != null && Str(Get(g.Inv, "name")).Length > 0 ? Str(Get(g.Inv, "name")) : Str(Get(first, "name")),
                ["startMm"] = start,
                ["lengthMm"] = end - start,
                ["widthMm"] = ordered.Max(x => Num(Get(x, "widthMm"), 1200)),
                ["rollerCount"] = ordered.Sum(x => (int)Math.Round(Num(Get(x, "rollerCount"), 21))),
                ["rollerPitchMm"] = Num(Get(first, "rollerPitchMm"), 300),
                ["rollerDiameterMm"] = Num(Get(first, "rollerDiameterMm"), 150),
                ["isEnabled"] = ordered.All(x => Bool(Get(x, "isEnabled"), true)) && (g.Inv == null || Bool(Get(g.Inv, "isEnabled"), true))
            };
            var dir = Get(first, "direction");
            if (dir != null) m["direction"] = dir.DeepClone();
            var driveSource = g.Inv ?? first;
            foreach (var key in new[] { "lowSpeed", "highSpeed", "acceleration", "deceleration", "defaultSpeed", "autoStopWhenEmpty" })
            {
                var v = Get(driveSource, key);
                if (v != null) m[key] = v.DeepClone();
            }
            modules.Add(m);
        }
        RemoveKey(node, "sections");
        RemoveKey(node, "inverters");
        node["sections"] = modules;
    }

    private static JsonNode? Get(JsonObject o, string key)
    {
        foreach (var kv in o)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static void RemoveKey(JsonObject o, string key)
    {
        foreach (var k in o.Select(kv => kv.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
            o.Remove(k);
    }

    private static string Str(JsonNode? n)
    {
        try { return n?.GetValue<string>() ?? string.Empty; } catch (InvalidOperationException) { return n?.ToString() ?? string.Empty; }
    }

    private static double Num(JsonNode? n, double def)
    {
        if (n == null) return def;
        try { return n.GetValue<double>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;
        }
    }

    private static bool Bool(JsonNode? n, bool def)
    {
        if (n == null) return def;
        try { return n.GetValue<bool>(); } catch (InvalidOperationException) { return def; }
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
