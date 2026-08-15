using Unison.Models;

namespace Unison.Services.Web;

/// <summary>
/// Shows and hides per-service WebView2 controls. Implemented by the main window host.
/// Called by WebServiceAdapter. Does not know about native HWNDs.
/// </summary>
public interface IWebViewHost
{
    event Action<string, string>? DocumentTitleChanged;

    event Action<string, bool>? DocumentAudioChanged;

    Task ShowAsync(ServiceDefinition definition, CancellationToken cancellationToken = default);

    Task HideAsync(string serviceId, CancellationToken cancellationToken = default);

    Task RemoveAsync(string serviceId, CancellationToken cancellationToken = default);

    void MuteOthers(string? exceptServiceId, bool mute);
}
