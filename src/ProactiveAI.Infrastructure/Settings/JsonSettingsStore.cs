using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Settings;

namespace ProactiveAI.Infrastructure.Settings;

/// <summary>Persists non-sensitive settings as JSON under %LOCALAPPDATA%\ProactiveAI.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _gate = new();

    public JsonSettingsStore(string directory, ILogger<JsonSettingsStore> log)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _log = log;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProactiveAI");

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new AppSettings().Normalize();
                }
                var json = File.ReadAllText(_path);
                return (JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings()).Normalize();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Settings could not be read ({Type}); defaults used", ex.GetType().Name);
                return new AppSettings().Normalize();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalize(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
