using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class PrivilegedSourceRequestTests
{
    [Fact]
    public void Validator_AllowsOnlyTheFourTypedMutations()
    {
        var request = new PrivilegedSourceRequest
        {
            RequestId = Guid.NewGuid(),
            Kind = SourceOperationKind.Refresh,
            SourceName = "winget"
        };

        var result = PrivilegedSourceRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("not allowlisted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsMixedOperationFields()
    {
        var request = PrivilegedSourceRequest.Remove("test") with
        {
            AddRequest = new AddPackageSourceRequest
            {
                Name = "hidden",
                Location = "https://example.test/index"
            },
            IsResetConfirmed = true
        };

        var result = PrivilegedSourceRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("Only add", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Errors,
            error => error.Contains("reset confirmation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsUnsafeAddLocation()
    {
        var request = PrivilegedSourceRequest.Add(new AddPackageSourceRequest
        {
            Name = "unsafe",
            Location = "http://example.test/index",
            Type = PackageSourceType.Rest
        });

        var result = PrivilegedSourceRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("HTTPS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatcher_MapsTypedAddWithoutCreatingACommandLine()
    {
        var service = new RecordingSourceManagementService();
        var add = new AddPackageSourceRequest
        {
            Name = "contoso",
            Location = "https://packages.contoso.test/index",
            Type = PackageSourceType.Rest,
            IsExplicit = true,
            TrustLevel = PackageSourceTrustLevel.None
        };

        var result = await PrivilegedSourceRequestDispatcher.DispatchAsync(
            service,
            PrivilegedSourceRequest.Add(add));

        Assert.True(result.IsSuccess);
        Assert.Same(add, service.AddedRequest);
        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task Dispatcher_DoesNotCallServiceForInvalidRequest()
    {
        var service = new RecordingSourceManagementService();
        var request = new PrivilegedSourceRequest
        {
            RequestId = Guid.NewGuid(),
            Kind = SourceOperationKind.Refresh,
            SourceName = "winget"
        };

        var result = await PrivilegedSourceRequestDispatcher.DispatchAsync(service, request);

        Assert.Equal(SourceOperationStatus.InvalidRequest, result.Status);
        Assert.Equal(0, service.CallCount);
    }

    private sealed class RecordingSourceManagementService : ISourceManagementService
    {
        public int CallCount { get; private set; }
        public AddPackageSourceRequest? AddedRequest { get; private set; }

        public Task<SourceManagementCapabilities> GetSourceManagementCapabilitiesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceManagementCapabilities { IsAvailable = true });

        public Task<IReadOnlyList<PackageSourceInfo>> GetSourceDetailsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSourceInfo>>(Array.Empty<PackageSourceInfo>());

        public Task<SourceOperationResult> RefreshSourceAsync(
            string sourceName,
            IProgress<SourceOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Record(SourceOperationKind.Refresh, sourceName);

        public Task<SourceOperationResult> AddSourceAsync(
            AddPackageSourceRequest request,
            IProgress<SourceOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            AddedRequest = request;
            return Record(SourceOperationKind.Add, request.Name);
        }

        public Task<SourceOperationResult> RemoveSourceAsync(
            string sourceName,
            IProgress<SourceOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Record(SourceOperationKind.Remove, sourceName);

        public Task<SourceOperationResult> ResetSourceAsync(
            ResetPackageSourceRequest request,
            IProgress<SourceOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Record(SourceOperationKind.Reset, request.SourceName);

        public Task<SourceOperationResult> SetSourceExplicitAsync(
            string sourceName,
            bool isExplicit,
            IProgress<SourceOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Record(SourceOperationKind.EditExplicit, sourceName);

        private Task<SourceOperationResult> Record(
            SourceOperationKind kind,
            string sourceName)
        {
            CallCount++;
            return Task.FromResult(new SourceOperationResult
            {
                OperationId = Guid.NewGuid(),
                Kind = kind,
                SourceName = sourceName,
                Status = SourceOperationStatus.Succeeded,
                Message = "Completed"
            });
        }
    }
}
