using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

public sealed class MsixInstalledAppProvider : IInstalledAppProvider
{
    public const string ProviderId = "msix";

    private readonly IMsixPackageReader _reader;

    public MsixInstalledAppProvider(IMsixPackageReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public string Id => ProviderId;
    public InstalledAppProviderKind Kind => InstalledAppProviderKind.Msix;

    public async Task<InstalledAppProviderResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var packages = await _reader.ReadCurrentUserPackagesAsync(cancellationToken);
        var installations = packages
            .Where(package => !string.IsNullOrWhiteSpace(package.PackageFullName))
            .Select(package => new Installation
            {
                Id = $"msix:{package.PackageFullName}",
                ProviderId = ProviderId,
                Provider = InstalledAppProviderKind.Msix,
                DisplayName = string.IsNullOrWhiteSpace(package.Name)
                    ? package.PackageFamilyName
                    : package.Name,
                Publisher = package.Publisher,
                Version = package.Version,
                Scope = InstallerScope.User,
                Architecture = package.Architecture,
                Aliases = string.IsNullOrWhiteSpace(package.PackageFamilyName)
                    ? Array.Empty<InstalledAppAlias>()
                    :
                    [
                        new InstalledAppAlias(
                            InstalledAppAliasKind.PackageFamilyName,
                            package.PackageFamilyName)
                    ],
                PackageFullName = package.PackageFullName,
                IsStoreApp = package.IsStoreApp,
                IsSystem = package.IsSystem,
                IsFramework = package.IsFramework,
                IsResourcePackage = package.IsResourcePackage,
                IsOptionalPackage = package.IsOptionalPackage,
                IsCurrentApp = package.IsCurrentApp,
                SupportsDirectRemoval = true
            })
            .ToArray();

        return new InstalledAppProviderResult { Installations = installations };
    }
}
