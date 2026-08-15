using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Unison.Discovery;
using Unison.Models;
using Unison.Windows;

namespace Unison.Services;

/// <summary>
/// Resolves sidebar icons from native executables or web favicons and caches PNG files.
/// Called by MainViewModel after the service list is built.
/// </summary>
public sealed class IconLoader
{
    private const int MinIconEdgePx = 64;
    private const int MinIconFileBytes = 200;

    private static readonly HashSet<string> PlaceholderIconHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Google s2 default globe (same bytes for calendar.google.com / mail.google.com at sz=128).
        "1631FF32E63A6DCC15469D14C7C94E42",
        // gstatic messages_48dp — older 96px product glyph; replace with 2022 Messages logo.
        "45345C5F8998615A278F150F560E0929",
        // GitHub 32px favicon.png
        "346E09471362F2907510A31812129CD2",
        // WhatsApp site favicon.ico (16px)
        "5A1A9C3FE6A387816B391B9867E86F4F",
        // Slack marketing favicon-32.png
        "9834AD3686266B9606E60C4B805C8ADD",
        // Notion favicon.ico (64px, often looks like a tiny "N" glyph)
        "C36351F4817C6D4ABFD93CB003B95B1D"
    };

    private static readonly Regex IconLinkRegex = new(
        """<link\s[^>]*rel=["'][^"']*(?:shortcut icon|apple-touch-icon|icon)[^"']*["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefRegex = new(
        """href=["']([^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<IconLoader> _logger;
    private readonly HttpClient _httpClient;
    private readonly WebServiceCatalog _catalog;
    private readonly string _cacheFolder;

    public IconLoader(ILogger<IconLoader> logger)
    {
        _logger = logger;
        _catalog = new WebServiceCatalog();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Unison",
            "Icons");
        Directory.CreateDirectory(_cacheFolder);
    }

    public async Task<string?> EnsureIconAsync(ServiceDefinition definition, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(definition.IconPath)
            && File.Exists(definition.IconPath)
            && !IsPlaceholderOrTinyIconFile(definition.IconPath))
        {
            return definition.IconPath;
        }

        var catalogUrl = _catalog.FindIconUrl(definition.Id)
            ?? _catalog.FindIconUrlByName(definition.Name);
        if (!string.IsNullOrWhiteSpace(catalogUrl))
        {
            definition.IconUrl = catalogUrl;
        }

        string? path = null;
        try
        {
            if (definition.ServiceType == ServiceType.WebService)
            {
                path = await TryFetchWebFaviconAsync(definition, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                path = TryExtractNativeIcon(definition);
                if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(definition.IconUrl))
                {
                    var dest = Path.Combine(_cacheFolder, Sanitize(definition.Id) + ".png");
                    path = await TryDownloadImageAsync(definition.IconUrl, dest, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load icon for {ServiceId}.", definition.Id);
        }

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            definition.IconPath = path;
            return path;
        }

        return null;
    }

    private string? TryExtractNativeIcon(ServiceDefinition definition)
    {
        var exe = ResolveExecutablePath(definition);
        if (string.IsNullOrWhiteSpace(exe)
            || exe.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var dest = Path.Combine(_cacheFolder, Sanitize(definition.Id) + ".png");
        var fromExtract = SaveExtractedIcon(exe, dest);
        if (fromExtract is not null)
        {
            return fromExtract;
        }

        return SaveShellFileIcon(exe, dest);
    }

    private static string? SaveExtractedIcon(string exe, string dest)
    {
        var large = new IntPtr[1];
        var extracted = Win32.ExtractIconEx(exe, 0, large, null, 1);
        if (extracted == 0 || large[0] == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = (Icon)Icon.FromHandle(large[0]).Clone();
            using var bitmap = icon.ToBitmap();
            bitmap.Save(dest, ImageFormat.Png);
            return dest;
        }
        finally
        {
            Win32.DestroyIcon(large[0]);
        }
    }

    private static string? SaveShellFileIcon(string path, string dest)
    {
        var info = new Win32.SHFILEINFO();
        var result = Win32.SHGetFileInfo(
            path,
            0,
            ref info,
            (uint)Marshal.SizeOf<Win32.SHFILEINFO>(),
            Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            using var bitmap = icon.ToBitmap();
            bitmap.Save(dest, ImageFormat.Png);
            return dest;
        }
        finally
        {
            Win32.DestroyIcon(info.hIcon);
        }
    }

    private static string? ResolveExecutablePath(ServiceDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.ExecutablePath)
            && !definition.ExecutablePath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            && File.Exists(definition.ExecutablePath))
        {
            return definition.ExecutablePath;
        }

        if (string.IsNullOrWhiteSpace(definition.ProcessName))
        {
            return null;
        }

        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(definition.ProcessName);
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // MainModule can throw for elevated processes.
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private async Task<string?> TryFetchWebFaviconAsync(ServiceDefinition definition, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Url) || !Uri.TryCreate(definition.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var dest = Path.Combine(_cacheFolder, Sanitize(definition.Id) + ".png");
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var candidates = new List<string>();

        var catalogUrl = _catalog.FindIconUrl(definition.Id)
            ?? _catalog.FindIconUrlByName(definition.Name);
        if (!string.IsNullOrWhiteSpace(catalogUrl))
        {
            candidates.Add(catalogUrl);
        }

        if (!string.IsNullOrWhiteSpace(definition.IconUrl))
        {
            candidates.Add(definition.IconUrl);
        }

        var googleHost = uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("gstatic.com", StringComparison.OrdinalIgnoreCase);
        if (!googleHost)
        {
            candidates.Add(origin + "/apple-touch-icon.png");
            candidates.Add(origin + "/apple-touch-icon-precomposed.png");
            candidates.Add(origin + "/favicon.png");
            candidates.Add(origin + "/favicon.ico");
        }

        foreach (var fromHtml in await TryIconsFromHtmlAsync(uri, cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(fromHtml);
        }

        if (!IsLocalOrPrivateHost(uri.Host))
        {
            candidates.Add($"https://www.google.com/s2/favicons?sz=128&domain={uri.Host}");
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var saved = await TryDownloadImageAsync(candidate, dest, cancellationToken).ConfigureAwait(false);
            if (saved is not null)
            {
                return saved;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> TryIconsFromHtmlAsync(Uri pageUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(pageUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (html.Length > 400_000)
            {
                html = html[..400_000];
            }

            var found = new List<string>();
            foreach (Match link in IconLinkRegex.Matches(html))
            {
                var hrefMatch = HrefRegex.Match(link.Value);
                if (!hrefMatch.Success)
                {
                    continue;
                }

                var href = hrefMatch.Groups[1].Value.Trim();
                if (Uri.TryCreate(pageUri, href, out var absolute))
                {
                    found.Add(absolute.ToString());
                }
            }

            return found
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(PreferHighResIconUrl)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse icons from {Url}.", pageUri);
            return [];
        }
    }

    private async Task<string?> TryDownloadImageAsync(string url, string pngDest, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length < MinIconFileBytes || bytes[0] == (byte)'<' || IsSvg(bytes) || IsPlaceholderIcon(bytes))
            {
                return null;
            }

            if (IsPng(bytes) || IsJpeg(bytes))
            {
                await File.WriteAllBytesAsync(pngDest, bytes, cancellationToken).ConfigureAwait(false);
                if (IsPlaceholderOrTinyIconFile(pngDest))
                {
                    TryDelete(pngDest);
                    return null;
                }

                return pngDest;
            }

            var icoDest = Path.ChangeExtension(pngDest, ".ico");
            await File.WriteAllBytesAsync(icoDest, bytes, cancellationToken).ConfigureAwait(false);
            if (TryConvertIcoToPng(icoDest, pngDest) && !IsPlaceholderOrTinyIconFile(pngDest))
            {
                TryDelete(icoDest);
                return pngDest;
            }

            TryDelete(icoDest);
            TryDelete(pngDest);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Icon download failed for {Url}.", url);
            return null;
        }
    }

    private static bool IsPlaceholderIcon(byte[] bytes)
    {
        var hash = Convert.ToHexString(MD5.HashData(bytes));
        if (PlaceholderIconHashes.Contains(hash))
        {
            return true;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            return image.Width < MinIconEdgePx || image.Height < MinIconEdgePx;
        }
        catch
        {
            return bytes.Length < 2048;
        }
    }

    private static bool IsPlaceholderOrTinyIconFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return bytes.Length < MinIconFileBytes || IsPlaceholderIcon(bytes);
        }
        catch
        {
            return true;
        }
    }

    private static int PreferHighResIconUrl(string url)
    {
        var score = 0;
        if (url.Contains("apple-touch", StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        if (url.Contains("192", StringComparison.OrdinalIgnoreCase)
            || url.Contains("180", StringComparison.OrdinalIgnoreCase)
            || url.Contains("256", StringComparison.OrdinalIgnoreCase)
            || url.Contains("512", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (url.Contains("96", StringComparison.OrdinalIgnoreCase)
            || url.Contains("128", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (url.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
            || url.Contains("favicon", StringComparison.OrdinalIgnoreCase))
        {
            score -= 20;
        }

        return score;
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool IsSvg(byte[] bytes)
    {
        var prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 64));
        return prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConvertIcoToPng(string icoPath, string pngPath)
    {
        foreach (var size in new[] { 256, 128, 64 })
        {
            try
            {
                using var icon = new Icon(icoPath, size, size);
                if (icon.Width < MinIconEdgePx || icon.Height < MinIconEdgePx)
                {
                    continue;
                }

                using var bitmap = icon.ToBitmap();
                if (bitmap.Width < MinIconEdgePx || bitmap.Height < MinIconEdgePx)
                {
                    continue;
                }

                bitmap.Save(pngPath, ImageFormat.Png);
                return File.Exists(pngPath);
            }
            catch
            {
                // Try the next size.
            }
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cache leftovers are overwritten on the next successful download.
        }
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

        return System.Net.IPAddress.TryParse(host, out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && address.GetAddressBytes() is { } bytes
            && (bytes[0] == 10
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }

    private static string Sanitize(string serviceId)
    {
        var chars = serviceId.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var value = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "icon" : value;
    }
}
