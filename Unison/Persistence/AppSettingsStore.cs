using System.Text.Json;
using Microsoft.Extensions.Logging;
using Unison.Models;

namespace Unison.Persistence;

/// <summary>
/// Loads and saves app settings as JSON in the user's local app data folder.
/// Called by MainWindow. Separate from services.json.
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ILogger<AppSettingsStore> _logger;
    private readonly string _filePath;

    public AppSettingsStore(ILogger<AppSettingsStore> logger)
    {
        _logger = logger;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unison");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is not null)
            {
                return loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read {Path}; using defaults.", _filePath);
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_filePath, json);
            _logger.LogInformation("Saved settings to {Path}.", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save settings to {Path}.", _filePath);
        }
    }
}
