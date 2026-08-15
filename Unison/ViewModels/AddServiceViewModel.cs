using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Unison.Discovery;
using Unison.Models;

namespace Unison.ViewModels;

/// <summary>
/// Unified Add Service search: catalog presets first, then a capped installed list,
/// URL/domain, DuckDuckGo, and Store. Bound by AddServicePage. Catalog is hints, not an allowlist.
/// </summary>
public sealed partial class AddServiceViewModel : ObservableObject
{
    private readonly InstalledApplicationScanner _scanner;
    private readonly WebServiceCatalog _catalog;
    private readonly WebsiteGuessService _guessService;
    private readonly AppSettings _settings;
    private readonly HashSet<string> _existingIds;
    private readonly HashSet<string> _existingWebNames;
    private readonly HashSet<string> _existingNativeNames;
    private readonly HashSet<string> _existingProcessNames;
    private List<CatalogItemViewModel> _installed = [];
    private List<CatalogItemViewModel> _hints = [];
    private CancellationTokenSource? _guessCts;
    private const int EmptyInstalledCap = 8;

    public AddServiceViewModel(
        InstalledApplicationScanner scanner,
        WebServiceCatalog catalog,
        WebsiteGuessService guessService,
        IEnumerable<ServiceDefinition> existingServices,
        AppSettings settings)
    {
        _scanner = scanner;
        _catalog = catalog;
        _guessService = guessService;
        _settings = settings;
        var existing = existingServices.ToList();
        _existingIds = existing.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _existingWebNames = existing
            .Where(s => s.ServiceType == ServiceType.WebService)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _existingNativeNames = existing
            .Where(s => s.ServiceType == ServiceType.NativeApplication)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _existingProcessNames = existing
            .Where(s => s.ServiceType == ServiceType.NativeApplication && !string.IsNullOrWhiteSpace(s.ProcessName))
            .SelectMany(s => s.ProcessName!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ReloadInstalledAndHints();
        RebuildResults();
    }

    public ObservableCollection<CatalogItemViewModel> Results { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _customName = string.Empty;

    [ObservableProperty]
    private string _customUrl = string.Empty;

    [ObservableProperty]
    private string _customError = string.Empty;

    public event EventHandler<ServiceDefinition>? ServiceChosen;

    public void RefreshInstalled()
    {
        ReloadInstalledAndHints();
        RebuildResults();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (LooksLikeUrl(value) && string.IsNullOrWhiteSpace(CustomUrl))
        {
            CustomUrl = NormalizeUrl(value);
            if (string.IsNullOrWhiteSpace(CustomName))
            {
                CustomName = GuessName(CustomUrl);
            }
        }

        RebuildResults();
        _ = GuessOfficialSiteAsync(value);
    }

    public void Choose(CatalogItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.StoreOnly)
        {
            _ = OpenStoreAsync(item);
            return;
        }

        if (item.Definition is null)
        {
            return;
        }

        ServiceChosen?.Invoke(this, item.Definition);
    }

    public Task OpenStoreAsync(CatalogItemViewModel? item)
    {
        if (item?.StoreUri is null)
        {
            return Task.CompletedTask;
        }

        return global::Windows.System.Launcher.LaunchUriAsync(item.StoreUri).AsTask();
    }

    public bool TryAddCustomWebsite()
    {
        CustomError = string.Empty;
        var name = CustomName.Trim();
        var url = NormalizeUrl(CustomUrl.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            name = GuessName(url);
        }

        if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            CustomError = "Enter a name and a website address like https://meet.google.com";
            return false;
        }

        var definition = CreateWebDefinition(name, uri);
        if (definition is null)
        {
            CustomError = "That service is already in Unison.";
            return false;
        }

        ServiceChosen?.Invoke(this, definition);
        return true;
    }

    private void ReloadInstalledAndHints()
    {
        _installed = [];
        foreach (var app in _scanner.Scan())
        {
            if (string.IsNullOrWhiteSpace(app.DisplayName)
                || IsAlreadyAdded(app.DisplayName, NativeId(app), app.ProcessName, native: true))
            {
                continue;
            }

            var definition = new ServiceDefinition
            {
                Id = NativeId(app),
                Name = app.DisplayName,
                ServiceType = ServiceType.NativeApplication,
                ExecutablePath = app.ExecutablePath,
                ProcessName = app.ProcessName,
                IconPath = app.IconPath,
                IconUrl = _catalog.FindIconUrlByName(app.DisplayName)
            };
            _settings.ApplyDefaultsTo(definition);
            _installed.Add(new CatalogItemViewModel(app.DisplayName, "Installed", definition));
        }

        _hints = [];
        foreach (var entry in _catalog.GetAll())
        {
            if (IsAlreadyAdded(entry.Name, entry.Id, null, native: false))
            {
                continue;
            }

            var definition = entry.ToDefinition();
            _settings.ApplyDefaultsTo(definition);
            _hints.Add(new CatalogItemViewModel(entry.Name, "Preset", definition, entry));
        }
    }

    private void RebuildResults(Uri? guessedSite = null)
    {
        var query = SearchText.Trim();
        var rows = new List<CatalogItemViewModel>();

        foreach (var hint in Filter(_hints, query))
        {
            rows.Add(hint);
        }

        var installedMatches = Filter(_installed, query);
        if (string.IsNullOrWhiteSpace(query))
        {
            installedMatches = installedMatches.Take(EmptyInstalledCap);
        }

        foreach (var item in installedMatches)
        {
            if (AlreadyListed(rows, item))
            {
                continue;
            }

            rows.Add(item);
        }

        if (TryParseWebAddress(query, out var typedUri))
        {
            var typed = CreateWebItem(GuessName(typedUri.ToString()), typedUri, "Website");
            if (typed is not null && !AlreadyListed(rows, typed))
            {
                rows.Add(typed);
            }
        }

        if (guessedSite is not null)
        {
            var guessed = CreateWebItem(GuessName(guessedSite.ToString()), guessedSite, "Website");
            if (guessed is not null && !AlreadyListed(rows, guessed))
            {
                rows.Add(guessed);
            }
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var storeUri = new Uri("ms-windows-store://search/?query=" + Uri.EscapeDataString(query));
            rows.Add(new CatalogItemViewModel(
                "Search Microsoft Store for “" + query + "”",
                "Store",
                definition: null,
                storeUri: storeUri,
                storeOnly: true));
        }

        Replace(Results, rows);
    }

    private static bool AlreadyListed(IReadOnlyList<CatalogItemViewModel> rows, CatalogItemViewModel item)
    {
        foreach (var row in rows)
        {
            if (string.Equals(row.Name, item.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.KindLabel, item.KindLabel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var existing = row.Definition;
            var candidate = item.Definition;
            if (existing is null || candidate is null)
            {
                continue;
            }

            if (string.Equals(existing.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(candidate.Url)
                && string.Equals(existing.Url, candidate.Url, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task GuessOfficialSiteAsync(string value)
    {
        _guessCts?.Cancel();
        _guessCts?.Dispose();
        var cts = new CancellationTokenSource();
        _guessCts = cts;

        var query = value.Trim();
        if (string.IsNullOrWhiteSpace(query) || LooksLikeUrl(query))
        {
            return;
        }

        try
        {
            await Task.Delay(400, cts.Token).ConfigureAwait(true);
            var uri = await _guessService.GuessOfficialSiteAsync(query, cts.Token).ConfigureAwait(true);
            if (uri is not null && SearchText.Trim().Equals(query, StringComparison.Ordinal))
            {
                RebuildResults(uri);
            }
        }
        catch (OperationCanceledException)
        {
            // Newer query is in flight.
        }
    }

    private CatalogItemViewModel? CreateWebItem(string name, Uri uri, string kind)
    {
        var definition = CreateWebDefinition(name, uri);
        return definition is null ? null : new CatalogItemViewModel(name, kind, definition);
    }

    private ServiceDefinition? CreateWebDefinition(string name, Uri uri)
    {
        var id = "web-" + new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (string.IsNullOrWhiteSpace(id) || id == "web-")
        {
            id = "web-" + Math.Abs(uri.ToString().GetHashCode());
        }

        if (IsAlreadyAdded(name, id, null, native: false))
        {
            return null;
        }

        var definition = new ServiceDefinition
        {
            Id = id,
            Name = name,
            ServiceType = ServiceType.WebService,
            Url = uri.ToString()
        };
        _settings.ApplyDefaultsTo(definition);
        return definition;
    }

    private static IEnumerable<CatalogItemViewModel> Filter(IEnumerable<CatalogItemViewModel> source, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return source;
        }

        return source.Where(item => item.Matches(query));
    }

    private static bool LooksLikeUrl(string value)
    {
        var text = value.Trim();
        return text.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || text.Contains("://", StringComparison.Ordinal)
            || (text.Contains('.', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal));
    }

    private static bool TryParseWebAddress(string query, out Uri uri)
    {
        uri = null!;
        if (!LooksLikeUrl(query))
        {
            return false;
        }

        if (!Uri.TryCreate(NormalizeUrl(query), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string NormalizeUrl(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return text;
    }

    private static string GuessName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(host) ? url : host;
    }

    private static void Replace(ObservableCollection<CatalogItemViewModel> target, IEnumerable<CatalogItemViewModel> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private bool IsAlreadyAdded(string name, string id, string? processName, bool native)
    {
        if (_existingIds.Contains(id))
        {
            return true;
        }

        if (native)
        {
            if (_existingNativeNames.Contains(name))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            return processName
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(_existingProcessNames.Contains);
        }

        return _existingWebNames.Contains(name);
    }

    private static string NativeId(InstalledApplication app)
    {
        var slug = new string(app.DisplayName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "app";
        }

        var hash = Math.Abs((app.ExecutablePath ?? app.DisplayName).ToUpperInvariant().GetHashCode());
        return $"native-{slug}-{hash}";
    }
}
