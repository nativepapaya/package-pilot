using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using Windows.ApplicationModel;
using Windows.System;

namespace PackagePilot.Windows.Services;

/// <summary>Enumerates current-user MSIX/Store registrations without mutating package state.</summary>
public sealed class WindowsMsixPackageReader : IMsixPackageReader
{
    public Task<IReadOnlyList<MsixPackageRecord>> ReadCurrentUserPackagesAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadCoreAsync(cancellationToken), cancellationToken);

    private static async Task<IReadOnlyList<MsixPackageRecord>> ReadCoreAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentFamilyName = TryGetCurrentFamilyName();
        var manager = new global::Windows.Management.Deployment.PackageManager();
        var results = new List<MsixPackageRecord>();

        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.IsFramework || package.IsResourcePackage || package.IsOptional)
            {
                continue;
            }

            var appEntries = await package.GetAppListEntriesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (appEntries.Count == 0)
            {
                // Dependency and servicing packages without a visible application entry
                // are not applications a person can meaningfully manage here.
                continue;
            }

            var id = package.Id;
            var familyName = id.FamilyName ?? string.Empty;
            var version = id.Version;

            results.Add(new MsixPackageRecord
            {
                PackageFullName = id.FullName ?? string.Empty,
                PackageFamilyName = familyName,
                Name = FirstNonEmpty(package.DisplayName, id.Name, familyName),
                Publisher = package.PublisherDisplayName ?? string.Empty,
                Version = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                Architecture = ToCoreArchitecture(id.Architecture),
                IsStoreApp = package.SignatureKind == PackageSignatureKind.Store,
                IsSystem = package.SignatureKind == PackageSignatureKind.System,
                IsFramework = package.IsFramework,
                IsResourcePackage = package.IsResourcePackage,
                IsOptionalPackage = package.IsOptional,
                IsCurrentApp = familyName.Equals(currentFamilyName, StringComparison.OrdinalIgnoreCase)
            });
        }

        return results;
    }

    private static string TryGetCurrentFamilyName()
    {
        try
        {
            return Package.Current.Id.FamilyName ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static PackageArchitecture ToCoreArchitecture(ProcessorArchitecture architecture) =>
        architecture switch
        {
            ProcessorArchitecture.X86 => PackageArchitecture.X86,
            ProcessorArchitecture.X64 => PackageArchitecture.X64,
            ProcessorArchitecture.Arm => PackageArchitecture.Arm,
            ProcessorArchitecture.Arm64 => PackageArchitecture.Arm64,
            ProcessorArchitecture.Neutral => PackageArchitecture.Neutral,
            _ => PackageArchitecture.Unknown
        };

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? string.Empty;
}
