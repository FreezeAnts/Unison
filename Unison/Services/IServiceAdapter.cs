using Unison.Models;

namespace Unison.Services;

/// <summary>
/// Starts, finds, shows, hides, and restores one service.
/// ServiceManager calls this. Native adapters call WindowDiscoveryService and NativeWindowManager.
/// </summary>
public interface IServiceAdapter
{
    ServiceDefinition Definition { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<IntPtr?> FindMainWindowAsync(CancellationToken cancellationToken = default);

    Task ActivateAsync(CancellationToken cancellationToken = default);

    Task DeactivateAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(CancellationToken cancellationToken = default);

    Task ApplyHostBoundsAsync(Unison.Windows.HostRect bounds, CancellationToken cancellationToken = default);
}
