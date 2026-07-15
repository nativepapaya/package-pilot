using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Reads only display metadata from the standard Add/Remove Programs locations. Uninstall command
/// values are intentionally neither read nor exposed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryUninstallReader : IRegistryUninstallReader
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly RegistryLocation[] Locations =
    [
        new(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU64", InstallerScope.User),
        new(RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU32", InstallerScope.User),
        new(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64", InstallerScope.Machine),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32", InstallerScope.Machine)
    ];

    public Task<RegistryUninstallReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry inventory provider requires Windows.");
        }

        return Task.Run(() => ReadCore(cancellationToken), cancellationToken);
    }

    private static RegistryUninstallReadResult ReadCore(CancellationToken cancellationToken)
    {
        var entries = new List<RegistryUninstallEntry>();
        var warnings = new List<string>();

        foreach (var location in Locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
                using var uninstallKey = baseKey.OpenSubKey(UninstallPath, writable: false);
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryReadEntry(uninstallKey, subKeyName, location, entries, warnings);
                }
            }
            catch (Exception exception) when (IsRecoverableRegistryFailure(exception))
            {
                warnings.Add($"{location.Id} could not be read: {exception.Message}");
            }
        }

        return new RegistryUninstallReadResult
        {
            Entries = entries,
            Warnings = warnings
        };
    }

    private static void TryReadEntry(
        RegistryKey uninstallKey,
        string subKeyName,
        RegistryLocation location,
        ICollection<RegistryUninstallEntry> entries,
        ICollection<string> warnings)
    {
        try
        {
            using var appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
            if (appKey is null || !IsVisibleApplication(appKey, out var displayName))
            {
                return;
            }

            entries.Add(new RegistryUninstallEntry
            {
                LocationId = location.Id,
                SubKeyName = subKeyName,
                DisplayName = displayName,
                Publisher = ReadString(appKey, "Publisher"),
                Version = ReadString(appKey, "DisplayVersion"),
                Scope = location.Scope,
                Architecture = location.View == RegistryView.Registry32
                    ? PackageArchitecture.X86
                    : Native64Architecture()
            });
        }
        catch (Exception exception) when (IsRecoverableRegistryFailure(exception))
        {
            warnings.Add($"{location.Id}\\{subKeyName} could not be read: {exception.Message}");
        }
    }

    private static bool IsVisibleApplication(RegistryKey key, out string displayName)
    {
        displayName = ReadString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName)
            || ReadInt32(key, "SystemComponent") == 1
            || !string.IsNullOrWhiteSpace(ReadString(key, "ParentKeyName")))
        {
            return false;
        }

        var releaseType = ReadString(key, "ReleaseType");
        return !releaseType.Equals("Update", StringComparison.OrdinalIgnoreCase)
            && !releaseType.Equals("Hotfix", StringComparison.OrdinalIgnoreCase)
            && !releaseType.Equals("Security Update", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadString(RegistryKey key, string name) =>
        key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?.ToString()?.Trim() ?? string.Empty;

    private static int ReadInt32(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int number => number,
            string text when int.TryParse(text, out var number) => number,
            _ => 0
        };
    }

    private static PackageArchitecture Native64Architecture() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? PackageArchitecture.Arm64
            : PackageArchitecture.X64;

    private static bool IsRecoverableRegistryFailure(Exception exception) =>
        exception is UnauthorizedAccessException
            or SecurityException
            or IOException;

    private sealed record RegistryLocation(
        RegistryHive Hive,
        RegistryView View,
        string Id,
        InstallerScope Scope);
}
