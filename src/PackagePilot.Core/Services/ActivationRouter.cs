using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Parses protocol and command-line activations into a deliberately read-only allowlist.
/// Package and source mutations are never accepted from outside the application.
/// </summary>
public sealed class ActivationRouter
{
    public const string ProtocolScheme = "packagepilot";
    public const int MaximumSearchLength = 512;

    private static readonly IReadOnlyDictionary<string, AppDestination> Destinations =
        new Dictionary<string, AppDestination>(StringComparer.OrdinalIgnoreCase)
        {
            ["discover"] = AppDestination.Discover,
            ["installed"] = AppDestination.Installed,
            ["updates"] = AppDestination.Updates,
            ["activity"] = AppDestination.Activity,
            ["settings"] = AppDestination.Settings,
            ["sources"] = AppDestination.Sources
        };

    public ActivationParseResult ParseProtocol(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, ProtocolScheme, StringComparison.OrdinalIgnoreCase))
        {
            return ActivationParseResult.Rejected("The activation URI does not use the Package Pilot protocol.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort)
        {
            return ActivationParseResult.Rejected("The activation URI contains unsupported authority data.");
        }

        var destinationText = uri.Host;
        if (string.IsNullOrWhiteSpace(destinationText))
        {
            destinationText = uri.AbsolutePath.Trim('/');
        }
        else if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            return ActivationParseResult.Rejected("The activation URI contains an unsupported path.");
        }

        if (!Destinations.TryGetValue(destinationText, out var destination))
        {
            return ActivationParseResult.Rejected("The activation destination is not supported.");
        }

        var queryValues = ParseQuery(uri.Query);
        if (queryValues.Error is not null)
        {
            return ActivationParseResult.Rejected(queryValues.Error);
        }

        if (queryValues.Values.Count > 0 && destination != AppDestination.Discover)
        {
            return ActivationParseResult.Rejected("Only Discover accepts query parameters.");
        }

        if (queryValues.Values.Keys.Any(key => !string.Equals(key, "query", StringComparison.OrdinalIgnoreCase)))
        {
            return ActivationParseResult.Rejected("The activation URI contains an unsupported query parameter.");
        }

        queryValues.Values.TryGetValue("query", out var searchQuery);
        return CreateNavigation(destination, searchQuery, checkForUpdates: false);
    }

    /// <summary>Parses arguments after the executable name.</summary>
    public ActivationParseResult ParseCommandLine(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return ActivationParseResult.Accepted(new AppActivationRequest());
        }

        var command = arguments[0].Trim();
        switch (command.ToLowerInvariant())
        {
            case "discover":
                return RequireNoArguments(arguments, AppDestination.Discover);
            case "search":
                if (arguments.Count < 2)
                {
                    return ActivationParseResult.Rejected("The search command requires a query.");
                }

                return CreateNavigation(
                    AppDestination.Discover,
                    string.Join(' ', arguments.Skip(1)),
                    checkForUpdates: false);

            case "updates":
                return RequireNoArguments(arguments, AppDestination.Updates);
            case "installed":
                return RequireNoArguments(arguments, AppDestination.Installed);
            case "sources":
                return RequireNoArguments(arguments, AppDestination.Sources);
            case "check":
                if (arguments.Count != 1)
                {
                    return ActivationParseResult.Rejected("The check command does not accept arguments.");
                }

                return ActivationParseResult.Accepted(new AppActivationRequest
                {
                    Destination = AppDestination.Updates,
                    CheckForUpdates = true
                });
            default:
                return ActivationParseResult.Rejected("The command is not supported. External mutation commands are not accepted.");
        }
    }

    public ActivationParseResult ParseNotificationArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return ActivationParseResult.Rejected("Notification activation data is missing.");
        }

        // Notifications emitted by Package Pilot use protocol-formatted arguments so the
        // same allowlist protects cold and warm activation paths.
        return Uri.TryCreate(arguments, UriKind.Absolute, out var uri)
            ? ParseProtocol(uri)
            : ActivationParseResult.Rejected("Notification activation data is malformed.");
    }

    private static ActivationParseResult RequireNoArguments(
        IReadOnlyList<string> arguments,
        AppDestination destination) =>
        arguments.Count == 1
            ? ActivationParseResult.Accepted(new AppActivationRequest { Destination = destination })
            : ActivationParseResult.Rejected("The command does not accept arguments.");

    private static ActivationParseResult CreateNavigation(
        AppDestination destination,
        string? searchQuery,
        bool checkForUpdates)
    {
        var normalizedQuery = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim();
        if (normalizedQuery?.Length > MaximumSearchLength)
        {
            return ActivationParseResult.Rejected($"Search queries cannot exceed {MaximumSearchLength} characters.");
        }

        return ActivationParseResult.Accepted(new AppActivationRequest
        {
            Destination = destination,
            SearchQuery = normalizedQuery,
            CheckForUpdates = checkForUpdates
        });
    }

    private static (Dictionary<string, string> Values, string? Error) ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return (values, null);
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var rawKey = separator < 0 ? pair : pair[..separator];
            var rawValue = separator < 0 ? string.Empty : pair[(separator + 1)..];
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return (values, "The activation URI contains invalid escaping.");
            }

            if (string.IsNullOrWhiteSpace(key) || !values.TryAdd(key, value))
            {
                return (values, "The activation URI contains a missing or duplicate query parameter.");
            }
        }

        return (values, null);
    }
}
