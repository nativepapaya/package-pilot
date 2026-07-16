namespace PackagePilot.Core.Services;

/// <summary>Raised when any package mutation is already active for the exact target.</summary>
public sealed class DuplicatePackageOperationException : InvalidOperationException
{
    public DuplicatePackageOperationException(Guid existingOperationId, string targetId)
        : base($"An equivalent operation for '{targetId}' is already queued or running.")
    {
        ExistingOperationId = existingOperationId;
        TargetId = targetId;
    }

    public Guid ExistingOperationId { get; }
    public string TargetId { get; }
}
