using PackagePilot.Core.Models;

namespace PackagePilot.Tests.Core;

public sealed class SourceManagementModelsTests
{
    [Fact]
    public void AddRequest_DefaultsToExplicitAndUntrustedPreIndexedSource()
    {
        var request = new AddPackageSourceRequest();

        Assert.True(request.IsExplicit);
        Assert.Equal(PackageSourceTrustLevel.None, request.TrustLevel);
        Assert.Equal(PackageSourceType.PreIndexed, request.Type);
        Assert.DoesNotContain(
            typeof(AddPackageSourceRequest).GetProperties(),
            property => property.Name.Contains("Header", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://packages.contoso.example/cache")]
    [InlineData("HTTPS://packages.contoso.example/api/v1?channel=stable")]
    [InlineData(@"\\server\share\winget")]
    public void AddRequestValidation_AllowsOnlySupportedRemoteLocations(string location)
    {
        var result = SourceRequestValidator.Validate(new AddPackageSourceRequest
        {
            Name = "contoso",
            Location = location,
            Type = PackageSourceType.Rest
        });

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://packages.contoso.example/cache")]
    [InlineData("file:///C:/sources/cache")]
    [InlineData("ftp://packages.contoso.example/cache")]
    [InlineData("https://user:secret@packages.contoso.example/cache")]
    [InlineData("https://packages.contoso.example/cache#fragment")]
    [InlineData(@"\\server")]
    [InlineData(@"\\?\C:\sources\cache")]
    [InlineData(@"\\server\share\..\cache")]
    public void AddRequestValidation_RejectsUnsafeLocations(string location)
    {
        var result = SourceRequestValidator.Validate(new AddPackageSourceRequest
        {
            Name = "contoso",
            Location = location
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AddRequestValidation_RejectsUnknownSourceType()
    {
        var result = SourceRequestValidator.Validate(new AddPackageSourceRequest
        {
            Name = "contoso",
            Location = "https://packages.contoso.example/cache",
            Type = PackageSourceType.Unknown
        });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("Microsoft.PreIndexed.Package", PackageSourceType.PreIndexed)]
    [InlineData("microsoft.rest", PackageSourceType.Rest)]
    [InlineData("unsupported", PackageSourceType.Unknown)]
    public void DeploymentTypeMapping_IsAllowlistedAndCaseInsensitive(
        string value,
        PackageSourceType expected)
    {
        Assert.Equal(expected, SourceRequestValidator.FromDeploymentType(value));
    }

    [Fact]
    public void ResetRequest_RequiresOneNamedSourceAndExplicitConfirmation()
    {
        Assert.False(SourceRequestValidator.Validate(new ResetPackageSourceRequest()).IsValid);
        Assert.False(SourceRequestValidator.Validate(new ResetPackageSourceRequest
        {
            SourceName = "winget"
        }).IsValid);
        Assert.True(SourceRequestValidator.Validate(new ResetPackageSourceRequest
        {
            SourceName = "winget",
            IsConfirmed = true
        }).IsValid);
    }

    [Fact]
    public void AgreementFingerprint_IsStableButInvalidatedByExactTermChanges()
    {
        var terms = new[]
        {
            new PackageAgreement
            {
                Label = "Terms",
                Text = "Exact source terms",
                AgreementUri = new Uri("https://contoso.example/terms")
            }
        };

        var first = SourceAgreementSnapshot.Create("contoso", terms);
        var repeated = SourceAgreementSnapshot.Create("contoso", terms);
        var changed = SourceAgreementSnapshot.Create("contoso", [terms[0] with
        {
            Text = "Exact source terms "
        }]);

        Assert.Equal(first.Fingerprint, repeated.Fingerprint);
        Assert.True(repeated.Matches(first.Fingerprint));
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        Assert.False(changed.Matches(first.Fingerprint));
    }

    [Theory]
    [InlineData(false, 11u, false, false)]
    [InlineData(true, 11u, false, false)]
    [InlineData(true, 12u, true, false)]
    [InlineData(true, 28u, true, true)]
    public void SourceCapabilities_GateContractAndElevationIndependently(
        bool available,
        uint contractVersion,
        bool supportsMutation,
        bool supportsEdit)
    {
        var capabilities = new SourceManagementCapabilities
        {
            IsAvailable = available,
            ContractVersion = contractVersion,
            IsCurrentProcessElevated = false
        };

        Assert.Equal(supportsMutation, capabilities.SupportsAdd);
        Assert.Equal(supportsMutation, capabilities.SupportsRemove);
        Assert.Equal(supportsMutation, capabilities.SupportsResetOne);
        Assert.Equal(supportsEdit, capabilities.SupportsExplicitEdit);
        Assert.False(capabilities.CanMutateInCurrentProcess);
        Assert.Equal(supportsMutation, capabilities.MutationsRequireElevation);
    }

    [Fact]
    public void AccessDeniedResult_ExplicitlyRequestsElevation()
    {
        var result = new SourceOperationResult
        {
            Status = SourceOperationStatus.AccessDenied
        };

        Assert.True(result.RequiresElevation);
        Assert.False(result.IsSuccess);
    }
}
