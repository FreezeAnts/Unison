using Unison.Discovery;
using Unison.Models;

namespace Unison.ViewModels;

/// <summary>
/// One row in the Add Service search results. Bound by AddServicePage.
/// </summary>
public sealed class CatalogItemViewModel
{
    private readonly WebServiceCatalogEntry? _webEntry;

    public CatalogItemViewModel(
        string name,
        string kindLabel,
        ServiceDefinition? definition,
        WebServiceCatalogEntry? webEntry = null,
        Uri? storeUri = null,
        bool storeOnly = false)
    {
        Name = name;
        KindLabel = kindLabel;
        Definition = definition;
        _webEntry = webEntry;
        StoreUri = storeUri ?? (webEntry?.ShowsGetApp == true ? webEntry.CreateStoreUri() : null);
        StoreOnly = storeOnly;
    }

    public string Name { get; }

    public string KindLabel { get; }

    public ServiceDefinition? Definition { get; }

    public bool StoreOnly { get; }

    public bool CanChoose => Definition is not null && !StoreOnly;

    public bool ShowGetApp => StoreUri is not null;

    public Microsoft.UI.Xaml.Visibility GetAppVisibility =>
        ShowGetApp ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Uri? StoreUri { get; }

    public bool Matches(string query)
    {
        if (_webEntry is not null)
        {
            return _webEntry.Matches(query);
        }

        return string.IsNullOrWhiteSpace(query)
            || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (Definition?.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Definition?.ProcessName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Definition?.Url?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
