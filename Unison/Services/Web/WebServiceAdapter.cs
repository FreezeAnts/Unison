using Microsoft.Extensions.Logging;
using Unison.Models;
using Unison.Windows;

namespace Unison.Services.Web;

/// <summary>
/// WebView2-backed service. Asks IWebViewHost to show a per-service browser profile.
/// Created by ServiceManager. Does not manage native windows.
/// </summary>
public sealed class WebServiceAdapter : IServiceAdapter
{
    private readonly IWebViewHost _webViewHost;
    private readonly ILogger<WebServiceAdapter> _logger;

    public WebServiceAdapter(
        ServiceDefinition definition,
        IWebViewHost webViewHost,
        ILogger<WebServiceAdapter> logger)
    {
        Definition = definition;
        _webViewHost = webViewHost;
        _logger = logger;
    }

    public ServiceDefinition Definition { get; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Definition.Url))
        {
            _logger.LogWarning("Web service {ServiceId} has no URL.", Definition.Id);
        }

        return Task.CompletedTask;
    }

    public Task<IntPtr?> FindMainWindowAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IntPtr?>(null);
    }

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        return _webViewHost.ShowAsync(Definition, cancellationToken);
    }

    public Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        return _webViewHost.HideAsync(Definition.Id, cancellationToken);
    }

    public Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        return _webViewHost.HideAsync(Definition.Id, cancellationToken);
    }

    public Task ApplyHostBoundsAsync(HostRect bounds, CancellationToken cancellationToken = default)
    {
        _ = bounds;
        return Task.CompletedTask;
    }
}
