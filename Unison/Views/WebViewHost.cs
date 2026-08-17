using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Unison.Models;
using Unison.Services.Web;

namespace Unison.Views;

/// <summary>
/// Creates one WebView2 per web service with its own user-data folder so logins stay separate.
/// Called by WebServiceAdapter through IWebViewHost. Adds controls to the content-area grid.
/// </summary>
public sealed class WebViewHost : IWebViewHost
{
    private readonly Grid _container;
    private readonly ILogger<WebViewHost> _logger;
    private readonly Dictionary<string, WebView2> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _profilesRoot;

    public event Action<string, string>? DocumentTitleChanged;

    public event Action<string, bool>? DocumentAudioChanged;

    public WebViewHost(Grid container, ILogger<WebViewHost> logger)
    {
        _container = container;
        _logger = logger;
        _profilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Unison",
            "WebProfiles");
        Directory.CreateDirectory(_profilesRoot);
    }

    public async Task ShowAsync(ServiceDefinition definition, CancellationToken cancellationToken = default)
    {
        try
        {
            var view = await GetOrCreateAsync(definition, cancellationToken).ConfigureAwait(true);
            foreach (var pair in _views)
            {
                pair.Value.Visibility = pair.Key.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            view.Visibility = Visibility.Visible;
            _logger.LogInformation("Showing WebView2 for {ServiceId}.", definition.Id);
            // #region agent log
            Unison.Windows.DebugSessionLog.Write("A", "WebViewHost.cs:ShowAsync", "WebView shown", new
            {
                id = definition.Id,
                url = definition.Url,
                hasCore = view.CoreWebView2 is not null,
                source = view.Source?.ToString(),
                viewCount = _views.Count,
                visible = view.Visibility.ToString()
            });
            // #endregion
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not show WebView2 for {ServiceId}.", definition.Id);
            // #region agent log
            Unison.Windows.DebugSessionLog.Write("A", "WebViewHost.cs:ShowAsync", "WebView failed", new
            {
                id = definition.Id,
                url = definition.Url,
                error = ex.GetType().Name,
                hresult = ex.HResult,
                message = ex.Message
            });
            // #endregion
            throw;
        }
    }

    public Task HideAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        if (_views.TryGetValue(serviceId, out var view))
        {
            view.Visibility = Visibility.Collapsed;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        if (_views.Remove(serviceId, out var view))
        {
            _container.Children.Remove(view);
            view.Close();
            _logger.LogInformation("Disposed WebView2 for {ServiceId}.", serviceId);
        }

        return Task.CompletedTask;
    }

    public void MuteOthers(string? exceptServiceId, bool mute)
    {
        foreach (var pair in _views)
        {
            if (pair.Value.CoreWebView2 is null)
            {
                continue;
            }

            var isExcepted = exceptServiceId is not null
                && pair.Key.Equals(exceptServiceId, StringComparison.OrdinalIgnoreCase);
            pair.Value.CoreWebView2.IsMuted = mute && !isExcepted;
            if (mute && !isExcepted)
            {
                pair.Value.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task<WebView2> GetOrCreateAsync(ServiceDefinition definition, CancellationToken _)
    {
        if (_views.TryGetValue(definition.Id, out var existing))
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(definition.Url) || !Uri.TryCreate(definition.Url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Web service {definition.Id} has no valid URL.");
        }

        var profileFolder = Path.Combine(_profilesRoot, Sanitize(definition.Id));
        Directory.CreateDirectory(profileFolder);

        string? runtimeVersion = null;
        string? runtimeError = null;
        try
        {
            runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            runtimeError = ex.GetType().Name + ": " + ex.Message;
        }

        // #region agent log
        Unison.Windows.DebugSessionLog.Write("B", "WebViewHost.cs:GetOrCreateAsync", "Before CreateAsync", new
        {
            id = definition.Id,
            url = definition.Url,
            profilesRoot = _profilesRoot,
            runtimeVersion,
            runtimeError,
            existingViews = _views.Count
        });
        // #endregion

        var webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _container.Children.Add(webView);
        _views[definition.Id] = webView;

        try
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", profileFolder);
            var environment = await CoreWebView2Environment.CreateAsync();
            await webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            _views.Remove(definition.Id);
            _container.Children.Remove(webView);
            try
            {
                webView.Close();
            }
            catch
            {
            }

            throw new InvalidOperationException(
                "Could not start the in-app browser. Install the Microsoft Edge WebView2 Runtime and try again.",
                ex);
        }
        webView.CoreWebView2.ProcessFailed += (_, args) =>
        {
            // #region agent log
            Unison.Windows.DebugSessionLog.Write("C", "WebViewHost.cs:ProcessFailed", "WebView process failed", new
            {
                id = definition.Id,
                kind = args.ProcessFailedKind.ToString(),
                reason = args.Reason.ToString()
            });
            // #endregion
        };
        webView.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            // #region agent log
            Unison.Windows.DebugSessionLog.Write("D", "WebViewHost.cs:NavigationCompleted", "Nav done", new
            {
                id = definition.Id,
                success = args.IsSuccess,
                status = args.HttpStatusCode,
                source = webView.Source?.ToString()
            });
            // #endregion
        };
        webView.CoreWebView2.ServerCertificateErrorDetected += (_, args) =>
        {
            if (IsLocalOrPrivateHost(uri.Host))
            {
                args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                _logger.LogInformation("Allowed local certificate error for {Host}.", uri.Host);
            }
        };
        webView.CoreWebView2.DocumentTitleChanged += (_, _) =>
        {
            DocumentTitleChanged?.Invoke(definition.Id, webView.CoreWebView2.DocumentTitle ?? string.Empty);
        };
        webView.CoreWebView2.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            DocumentAudioChanged?.Invoke(definition.Id, webView.CoreWebView2.IsDocumentPlayingAudio);
        };
        webView.CoreWebView2.PermissionRequested += (_, args) =>
        {
            if (args.PermissionKind == CoreWebView2PermissionKind.Notifications)
            {
                args.State = CoreWebView2PermissionState.Allow;
            }
        };
        webView.Source = uri;
        _logger.LogInformation("Created WebView2 for {ServiceId} at {Url} (profile {Profile}).", definition.Id, uri, profileFolder);
        return webView;
    }

    private static string Sanitize(string serviceId)
    {
        var chars = serviceId.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var value = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "web" : value;
    }

    private static bool IsLocalOrPrivateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && (bytes[0] == 10
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }
}
