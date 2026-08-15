using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using global::Windows.ApplicationModel;
using global::Windows.Management.Deployment;

namespace Unison.Discovery;

/// <summary>
/// Finds installed Windows applications from Start Menu shortcuts, Store packages, and app execution aliases.
/// Called by AddServiceViewModel. UI never talks to PackageManager directly.
/// </summary>
public sealed class InstalledApplicationScanner
{
    private static readonly HashSet<string> AliasSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameBar", "GameBarFTW", "MicrosoftEdge", "MicrosoftEdgeUpdate", "msedge",
        "python", "python3", "pythonw", "winget", "WindowsPackageManagerServer",
        "ApplicationFrameHost", "SystemSettings"
    };

    private readonly ILogger<InstalledApplicationScanner> _logger;

    public InstalledApplicationScanner(ILogger<InstalledApplicationScanner> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<InstalledApplication> Scan()
    {
        var byKey = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);

        AddStorePackages(byKey);
        AddStartMenuShortcuts(byKey);
        AddExecutionAliases(byKey);
        AddWhatsAppWindowsAppsFallbacks(byKey);

        var results = byKey.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _logger.LogInformation("Found {Count} installed applications.", results.Count);
        return results;
    }

    private void AddStartMenuShortcuts(Dictionary<string, InstalledApplication> byKey)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read Start Menu folder {Root}.", root);
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var app = TryReadShortcut(shortcut);
                if (app?.ExecutablePath is null)
                {
                    continue;
                }

                byKey.TryAdd(app.ExecutablePath, app);
            }
        }
    }

    private void AddExecutionAliases(Dictionary<string, InstalledApplication> byKey)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");
        if (!Directory.Exists(folder))
        {
            return;
        }

        IEnumerable<string> exes;
        try
        {
            exes = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read WindowsApps aliases.");
            return;
        }

        foreach (var exe in exes)
        {
            var name = Path.GetFileNameWithoutExtension(exe);
            if (string.IsNullOrWhiteSpace(name)
                || AliasSkip.Contains(name)
                || LooksLikeUninstaller(name)
                || (name.Contains('.', StringComparison.Ordinal) && !ContainsWhatsAppToken(name)))
            {
                continue;
            }

            if (byKey.Values.Any(existing => existing.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            byKey.TryAdd(exe, new InstalledApplication(name, exe, name, null));
        }
    }

    private void AddStorePackages(Dictionary<string, InstalledApplication> byKey)
    {
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                if (package.IsFramework || package.IsResourcePackage)
                {
                    continue;
                }

                if (package.SignatureKind is not PackageSignatureKind.Store
                    and not PackageSignatureKind.Developer
                    and not PackageSignatureKind.None)
                {
                    continue;
                }

                var packageName = package.Id.Name;
                var familyName = package.Id.FamilyName;
                IReadOnlyList<global::Windows.ApplicationModel.Core.AppListEntry> entries = [];
                try
                {
                    entries = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "No app list entries for {Package}.", packageName);
                }

                if (entries.Count == 0)
                {
                    TryAddStorePackageFallback(byKey, package, packageName, familyName, displayFromEntry: null, aumid: null, iconPath: null);
                    continue;
                }

                foreach (var entry in entries)
                {
                    string? displayFromEntry = null;
                    try
                    {
                        displayFromEntry = entry.DisplayInfo.DisplayName;
                    }
                    catch
                    {
                        // Use package identity below.
                    }

                    var aumid = string.IsNullOrWhiteSpace(entry.AppUserModelId) ? null : entry.AppUserModelId;
                    var iconPath = string.IsNullOrWhiteSpace(displayFromEntry)
                        ? null
                        : TrySavePackageLogo(entry, displayFromEntry);
                    TryAddStorePackageFallback(byKey, package, packageName, familyName, displayFromEntry, aumid, iconPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate Store packages.");
        }
    }

    private static string? TrySavePackageLogo(global::Windows.ApplicationModel.Core.AppListEntry entry, string displayName)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Unison",
                "Icons");
            Directory.CreateDirectory(folder);
            var dest = Path.Combine(folder, SanitizeFileName(displayName) + "-pkg.png");
            var logo = entry.DisplayInfo.GetLogo(new global::Windows.Foundation.Size(64, 64));
            var operation = logo.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            using (operation)
            using (var file = File.Create(dest))
            {
                operation.AsStreamForRead().CopyTo(file);
            }

            return File.Exists(dest) && new FileInfo(dest).Length > 0 ? dest : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var value = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "app" : value;
    }

    private static string? TryFindPackageExecutable(Package package, string displayName)
    {
        string installPath;
        try
        {
            installPath = package.InstalledLocation.Path;
        }
        catch
        {
            return null;
        }

        if (!Directory.Exists(installPath))
        {
            return null;
        }

        try
        {
            var exes = Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
                .Where(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    return !LooksLikeUninstaller(name)
                        && !name.Contains("crash", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("update", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            var preferred = exes.FirstOrDefault(path =>
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                return fileName.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains(displayName.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase)
                    || displayName.Contains(fileName, StringComparison.OrdinalIgnoreCase);
            });
            return preferred ?? exes.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string GuessProcessName(string displayName)
    {
        var slug = new string(displayName.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(slug) ? displayName : slug;
    }

    private InstalledApplication? TryReadShortcut(string shortcutPath)
    {
        try
        {
            var displayName = Path.GetFileNameWithoutExtension(shortcutPath);
            if (LooksLikeUninstaller(displayName))
            {
                return null;
            }

            var target = ResolveShortcutTarget(shortcutPath);
            if (string.IsNullOrWhiteSpace(target)
                || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(target)
                || LooksLikeUninstaller(Path.GetFileNameWithoutExtension(target)))
            {
                return null;
            }

            return new InstalledApplication(
                displayName,
                target,
                Path.GetFileNameWithoutExtension(target),
                null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipping shortcut {Path}.", shortcutPath);
            return null;
        }
    }

    private void TryAddStorePackageFallback(
        Dictionary<string, InstalledApplication> byKey,
        Package package,
        string packageName,
        string familyName,
        string? displayFromEntry,
        string? aumid,
        string? iconPath)
    {
        var displayName = ResolvePackageDisplayName(displayFromEntry, packageName);
        if (string.IsNullOrWhiteSpace(displayName) || LooksLikeUninstaller(displayName))
        {
            return;
        }

        var exe = TryFindPackageExecutable(package, displayName)
            ?? TryFindPackageExecutable(package, packageName);
        var aumidLaunch = !string.IsNullOrWhiteSpace(aumid)
            ? "shell:AppsFolder\\" + aumid
            : (!string.IsNullOrWhiteSpace(familyName) ? "shell:AppsFolder\\" + familyName + "!App" : null);
        var launch = aumidLaunch ?? exe;
        if (string.IsNullOrWhiteSpace(launch))
        {
            return;
        }

        var processParts = new[]
        {
            exe is not null ? Path.GetFileNameWithoutExtension(exe) : null,
            ContainsWhatsAppToken(packageName) || ContainsWhatsAppToken(displayName) ? "WhatsApp" : null,
            GuessProcessName(displayName)
        }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct(StringComparer.OrdinalIgnoreCase);
        var processName = string.Join(",", processParts);
        byKey.TryAdd(launch, new InstalledApplication(displayName, launch, processName, iconPath));
    }

    private void AddWhatsAppWindowsAppsFallbacks(Dictionary<string, InstalledApplication> byKey)
    {
        if (byKey.Values.Any(app => ContainsWhatsAppToken(app.DisplayName)
            || ContainsWhatsAppToken(app.ProcessName)
            || ContainsWhatsAppToken(app.ExecutablePath)))
        {
            return;
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var exe in EnumerateFilesSafe(folder, "*WhatsApp*.exe"))
        {
            var name = Path.GetFileNameWithoutExtension(exe);
            if (string.IsNullOrWhiteSpace(name) || LooksLikeUninstaller(name))
            {
                continue;
            }

            var display = ContainsWhatsAppToken(name) ? "WhatsApp" : name;
            byKey.TryAdd(exe, new InstalledApplication(display, exe, "WhatsApp", null));
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files = [];
            try
            {
                files = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                // Junctions and Store package folders often deny listing.
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> subs = [];
            try
            {
                subs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subs)
            {
                stack.Push(sub);
            }
        }
    }

    private static string ResolvePackageDisplayName(string? displayFromEntry, string packageName)
    {
        if (!string.IsNullOrWhiteSpace(displayFromEntry) && !LooksLikeUnresolvedResource(displayFromEntry))
        {
            return displayFromEntry;
        }

        if (ContainsWhatsAppToken(packageName) || ContainsWhatsAppToken(displayFromEntry))
        {
            return "WhatsApp";
        }

        var leaf = packageName;
        var dot = packageName.LastIndexOf('.');
        if (dot >= 0 && dot < packageName.Length - 1)
        {
            leaf = packageName[(dot + 1)..];
        }

        return string.IsNullOrWhiteSpace(leaf) ? packageName : leaf;
    }

    private static bool LooksLikeUnresolvedResource(string name) =>
        name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWhatsAppToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeUninstaller(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("remove ", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveShortcutTarget(string shortcutPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null)
        {
            return null;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath]);
            return shortcut?
                .GetType()
                .InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)
                as string;
        }
        finally
        {
            if (shortcut is not null)
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null)
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}

public sealed record InstalledApplication(
    string DisplayName,
    string? ExecutablePath,
    string? ProcessName,
    string? IconPath);
