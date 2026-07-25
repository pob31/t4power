using System.Text.Json;
using System.Text.Json.Serialization;

namespace T4Power.Core.Model;

/// <summary>
/// Loads and saves <see cref="AppConfig"/>. The service is the only writer — the UI and CLI
/// mutate config by sending commands over the pipe. One writer means no file locking, no ACL
/// juggling for unelevated clients, and no lost updates.
/// </summary>
public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    readonly string _path;
    readonly object _gate = new();

    public ConfigStore(string? path = null) => _path = path ?? Paths.ConfigFile;

    public string Path => _path;

    /// <summary>
    /// Reads the config, returning defaults when the file is missing. A corrupt file is moved
    /// aside rather than deleted — losing a hand-edited config silently would be worse than the
    /// clutter of a .bad file.
    /// </summary>
    public AppConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new AppConfig();

            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                TryQuarantine();
                return new AppConfig();
            }
        }
    }

    /// <summary>
    /// Writes atomically: a torn config.json would leave the service unable to start, so the
    /// content lands in a temp file and is then moved into place.
    /// </summary>
    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    void TryQuarantine()
    {
        try
        {
            var bad = $"{_path}.bad";
            File.Move(_path, bad, overwrite: true);
        }
        catch (IOException)
        {
            // Nothing useful to do; Load() falls back to defaults either way.
        }
    }
}
