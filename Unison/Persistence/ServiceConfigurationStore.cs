using System.Text.Json;
using Microsoft.Extensions.Logging;
using Unison.Models;

namespace Unison.Persistence;

/// <summary>
/// Loads and saves configured services as JSON in the user's local app data folder.
/// Called by MainViewModel. UI classes never read the file directly.
/// </summary>
public sealed class ServiceConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ILogger<ServiceConfigurationStore> _logger;
    private readonly string _filePath;

    public ServiceConfigurationStore(ILogger<ServiceConfigurationStore> logger)
    {
        _logger = logger;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unison");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "services.json");
    }

    public IReadOnlyList<ServiceDefinition> Load()
    {
        if (!File.Exists(_filePath))
        {
            var defaults = CreateDefaults();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<ServiceDefinition>>(json, JsonOptions);
            if (loaded is { Count: > 0 })
            {
                if (MigrateHomeAssistantUrl(loaded))
                {
                    Save(loaded);
                }

                return loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read {Path}; using defaults.", _filePath);
        }

        return CreateDefaults();
    }

    public void Save(IReadOnlyList<ServiceDefinition> services)
    {
        try
        {
            var json = JsonSerializer.Serialize(services, JsonOptions);
            File.WriteAllText(_filePath, json);
            _logger.LogInformation("Saved {Count} services to {Path}.", services.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save services to {Path}.", _filePath);
        }
    }

    private static bool MigrateHomeAssistantUrl(List<ServiceDefinition> services)
    {
        var changed = false;
        foreach (var service in services)
        {
            if (!string.Equals(service.Id, "home-assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(service.Url, "https://homeassistant.local:8123", StringComparison.OrdinalIgnoreCase)
                || string.Equals(service.Url, "https://homeassistant.local:8123/", StringComparison.OrdinalIgnoreCase))
            {
                service.Url = "http://homeassistant.local:8123";
                changed = true;
            }
        }

        return changed;
    }

    private static List<ServiceDefinition> CreateDefaults() =>
    [
        new()
        {
            Id = "outlook",
            Name = "Outlook",
            ServiceType = ServiceType.NativeApplication,
            ProcessName = "OUTLOOK,olk,HxOutlook",
            NotificationAppId = "Outlook",
            ShowNotificationBadge = true
        },
        new()
        {
            Id = "teams",
            Name = "Teams",
            ServiceType = ServiceType.NativeApplication,
            ProcessName = "ms-teams,Teams",
            NotificationAppId = "Teams",
            ShowNotificationBadge = true
        }
    ];
}
