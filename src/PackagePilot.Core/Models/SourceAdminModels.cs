namespace PackagePilot.Core.Models;

/// <summary>
/// The complete allowlisted request surface accepted by the elevated source helper. It
/// intentionally has no arbitrary command, argument, environment, or custom-header fields.
/// </summary>
public sealed record PrivilegedSourceRequest
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSourceNameLength = 128;
    public const int MaximumSourceLocationLength = 2048;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid RequestId { get; init; }
    public SourceOperationKind Kind { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public AddPackageSourceRequest? AddRequest { get; init; }
    public bool? IsExplicit { get; init; }
    public bool IsResetConfirmed { get; init; }

    public static PrivilegedSourceRequest Add(
        AddPackageSourceRequest request,
        Guid? requestId = null) =>
        new()
        {
            RequestId = requestId ?? Guid.NewGuid(),
            Kind = SourceOperationKind.Add,
            AddRequest = request
        };

    public static PrivilegedSourceRequest Remove(
        string sourceName,
        Guid? requestId = null) =>
        new()
        {
            RequestId = requestId ?? Guid.NewGuid(),
            Kind = SourceOperationKind.Remove,
            SourceName = sourceName
        };

    public static PrivilegedSourceRequest Reset(
        string sourceName,
        bool isConfirmed,
        Guid? requestId = null) =>
        new()
        {
            RequestId = requestId ?? Guid.NewGuid(),
            Kind = SourceOperationKind.Reset,
            SourceName = sourceName,
            IsResetConfirmed = isConfirmed
        };

    public static PrivilegedSourceRequest EditExplicit(
        string sourceName,
        bool isExplicit,
        Guid? requestId = null) =>
        new()
        {
            RequestId = requestId ?? Guid.NewGuid(),
            Kind = SourceOperationKind.EditExplicit,
            SourceName = sourceName,
            IsExplicit = isExplicit
        };
}

public sealed record PrivilegedSourceRequestValidationResult
{
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool IsValid => Errors.Count == 0;

    public static PrivilegedSourceRequestValidationResult Valid { get; } = new();
}

public static class PrivilegedSourceRequestValidator
{
    public static PrivilegedSourceRequestValidationResult Validate(
        PrivilegedSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (request.SchemaVersion != PrivilegedSourceRequest.CurrentSchemaVersion)
        {
            errors.Add("The privileged source request schema is not supported.");
        }

        if (request.RequestId == Guid.Empty)
        {
            errors.Add("A non-empty request identifier is required.");
        }

        if (request.Kind is not SourceOperationKind.Add
            and not SourceOperationKind.Remove
            and not SourceOperationKind.Reset
            and not SourceOperationKind.EditExplicit)
        {
            errors.Add("The requested source operation is not allowlisted for elevation.");
        }

        switch (request.Kind)
        {
            case SourceOperationKind.Add:
                ValidateAdd(request, errors);
                break;
            case SourceOperationKind.Remove:
                ValidateNamedMutation(request, errors);
                if (request.IsResetConfirmed)
                {
                    errors.Add("Remove requests cannot carry reset confirmation.");
                }

                break;
            case SourceOperationKind.Reset:
                ValidateNamedMutation(request, errors);
                if (!request.IsResetConfirmed)
                {
                    errors.Add("Resetting a predefined source requires explicit confirmation.");
                }

                break;
            case SourceOperationKind.EditExplicit:
                ValidateNamedMutation(request, errors);
                if (request.IsExplicit is null)
                {
                    errors.Add("An explicit-source value is required.");
                }

                if (request.IsResetConfirmed)
                {
                    errors.Add("Edit requests cannot carry reset confirmation.");
                }

                break;
        }

        return errors.Count == 0
            ? PrivilegedSourceRequestValidationResult.Valid
            : new PrivilegedSourceRequestValidationResult { Errors = errors };
    }

    private static void ValidateAdd(
        PrivilegedSourceRequest request,
        ICollection<string> errors)
    {
        if (!string.IsNullOrEmpty(request.SourceName)
            || request.IsExplicit is not null
            || request.IsResetConfirmed)
        {
            errors.Add("Add requests contain fields that are not valid for that operation.");
        }

        if (request.AddRequest is null)
        {
            errors.Add("An add-source request is required.");
            return;
        }

        var validation = SourceRequestValidator.Validate(request.AddRequest);
        foreach (var error in validation.Errors)
        {
            errors.Add(error);
        }

        ValidateSourceName(request.AddRequest.Name, errors);
        if ((request.AddRequest.Location?.Length ?? 0)
            > PrivilegedSourceRequest.MaximumSourceLocationLength)
        {
            errors.Add("The source location is too long.");
        }
    }

    private static void ValidateNamedMutation(
        PrivilegedSourceRequest request,
        ICollection<string> errors)
    {
        if (request.AddRequest is not null)
        {
            errors.Add("Only add requests can contain add-source details.");
        }

        ValidateSourceName(request.SourceName, errors);
    }

    private static void ValidateSourceName(string? value, ICollection<string> errors)
    {
        var validation = SourceRequestValidator.ValidateSourceName(value);
        foreach (var error in validation.Errors)
        {
            errors.Add(error);
        }

        if (value is null)
        {
            return;
        }

        if (value.Length > PrivilegedSourceRequest.MaximumSourceNameLength)
        {
            errors.Add("The source name is too long.");
        }

        if (value.Any(char.IsControl))
        {
            errors.Add("The source name contains control characters.");
        }
    }
}

public sealed record PrivilegedSourceResponse
{
    public int SchemaVersion { get; init; } = PrivilegedSourceRequest.CurrentSchemaVersion;
    public Guid RequestId { get; init; }
    public SourceOperationResult Result { get; init; } = new()
    {
        Status = SourceOperationStatus.Failed,
        Message = "The elevated source helper did not return a result."
    };
}
