using Microsoft.Extensions.Logging;
using Unison.Models;
using Unison.Services.Native;
using Unison.Services.Web;
using Unison.Windows;

namespace Unison.Services;

/// <summary>
/// Owns the list of adapters and switches the active service.
/// Called by MainViewModel. Creates adapters and forwards content-host bounds to the selected adapter.
/// </summary>
public sealed class ServiceManager
{
    private readonly WindowDiscoveryService _windowDiscovery;
    private readonly NativeWindowManager _nativeWindowManager;
    private readonly ProcessLocator _processLocator;
    private readonly IWebViewHost _webViewHost;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ServiceManager> _logger;
    private readonly Dictionary<string, IServiceAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _selectCts;
    private HostRect _hostBounds;

    public ServiceManager(
        WindowDiscoveryService windowDiscovery,
        NativeWindowManager nativeWindowManager,
        ProcessLocator processLocator,
        IWebViewHost webViewHost,
        ILoggerFactory loggerFactory)
    {
        _windowDiscovery = windowDiscovery;
        _nativeWindowManager = nativeWindowManager;
        _processLocator = processLocator;
        _webViewHost = webViewHost;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ServiceManager>();
    }

    public event Action<string, string>? HostedWindowTitleChanged;

    public string? SelectedServiceId { get; private set; }

    public IReadOnlyCollection<IServiceAdapter> Adapters => _adapters.Values;

    public IServiceAdapter Register(ServiceDefinition definition)
    {
        var adapter = CreateAdapter(definition);
        _adapters[definition.Id] = adapter;
        _logger.LogInformation("Registered service {ServiceId} ({ServiceType}).", definition.Id, definition.ServiceType);
        return adapter;
    }

    public async Task RemoveAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        if (!_adapters.TryGetValue(serviceId, out var adapter))
        {
            return;
        }

        await adapter.RestoreAsync(cancellationToken).ConfigureAwait(true);
        await adapter.DeactivateAsync(cancellationToken).ConfigureAwait(true);
        if (adapter.Definition.ServiceType == ServiceType.WebService)
        {
            await _webViewHost.RemoveAsync(serviceId, cancellationToken).ConfigureAwait(true);
        }

        _adapters.Remove(serviceId);
        if (string.Equals(SelectedServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedServiceId = null;
        }

        _logger.LogInformation("Removed service {ServiceId}.", serviceId);
    }

    public async Task SelectAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        _selectCts?.Cancel();
        _selectCts?.Dispose();
        _selectCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _selectCts.Token);
        var token = linked.Token;

        if (SelectedServiceId is { } currentId &&
            _adapters.TryGetValue(currentId, out var current) &&
            !string.Equals(currentId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            await current.DeactivateAsync(CancellationToken.None).ConfigureAwait(true);
        }

        if (!_adapters.TryGetValue(serviceId, out var next))
        {
            _logger.LogWarning("Cannot select unknown service {ServiceId}.", serviceId);
            return;
        }

        SelectedServiceId = serviceId;
        try
        {
            await next.StartAsync(token).ConfigureAwait(true);
            if (_hostBounds.IsValid)
            {
                await next.ApplyHostBoundsAsync(_hostBounds, token).ConfigureAwait(true);
            }

            await next.ActivateAsync(token).ConfigureAwait(true);
            if (next is NativeApplicationAdapter native)
            {
                var title = native.ReadWindowTitle();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    HostedWindowTitleChanged?.Invoke(serviceId, title);
                }
            }
            _logger.LogInformation("Selected service {ServiceId}.", serviceId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Selection of {ServiceId} was cancelled.", serviceId);
        }
    }

    public async Task UpdateHostBoundsAsync(HostRect bounds, CancellationToken cancellationToken = default)
    {
        _hostBounds = bounds;
        if (SelectedServiceId is { } id && _adapters.TryGetValue(id, out var adapter))
        {
            await adapter.ApplyHostBoundsAsync(bounds, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters.Values)
        {
            await adapter.RestoreAsync(cancellationToken).ConfigureAwait(true);
        }

        _nativeWindowManager.RestoreAll();
        _logger.LogInformation("Restored all managed native windows.");
    }

    private IServiceAdapter CreateAdapter(ServiceDefinition definition)
    {
        if (definition.ServiceType == ServiceType.WebService)
        {
            return new WebServiceAdapter(definition, _webViewHost, _loggerFactory.CreateLogger<WebServiceAdapter>());
        }

        return definition.Id switch
        {
            "outlook" => new OutlookServiceAdapter(
                definition,
                _windowDiscovery,
                _nativeWindowManager,
                _processLocator,
                _loggerFactory.CreateLogger<OutlookServiceAdapter>()),
            "teams" => new TeamsServiceAdapter(
                definition,
                _windowDiscovery,
                _nativeWindowManager,
                _processLocator,
                _loggerFactory.CreateLogger<TeamsServiceAdapter>()),
            _ => new GenericNativeServiceAdapter(
                definition,
                _windowDiscovery,
                _nativeWindowManager,
                _processLocator,
                _loggerFactory.CreateLogger<GenericNativeServiceAdapter>())
        };
    }
}

/// <summary>
/// Fallback native adapter for services that are not Outlook or Teams yet.
/// </summary>
internal sealed class GenericNativeServiceAdapter : NativeApplicationAdapter
{
    public GenericNativeServiceAdapter(
        ServiceDefinition definition,
        WindowDiscoveryService windowDiscovery,
        NativeWindowManager nativeWindowManager,
        ProcessLocator processLocator,
        ILogger<GenericNativeServiceAdapter> logger)
        : base(definition, windowDiscovery, nativeWindowManager, processLocator, logger)
    {
    }
}
