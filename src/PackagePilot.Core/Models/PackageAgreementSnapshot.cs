using System.Security.Cryptography;
using System.Text;

namespace PackagePilot.Core.Models;

/// <summary>
/// Exact fingerprint of the package identity and the package terms currently presented by
/// WinGet. Consent is valid only while every fingerprinted field remains unchanged.
/// </summary>
public sealed record PackageAgreementSnapshot
{
    public PackageKey Package { get; init; } = PackageKey.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public IReadOnlyList<PackageAgreement> Agreements { get; init; } =
        Array.Empty<PackageAgreement>();

    public bool HasAgreements => Agreements.Count > 0;

    public bool Matches(string? acceptedFingerprint) =>
        IsValidFingerprint(acceptedFingerprint)
        && string.Equals(Fingerprint, acceptedFingerprint, StringComparison.Ordinal);

    public static PackageAgreementSnapshot Create(
        PackageKey package,
        IEnumerable<PackageAgreement> agreements)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(agreements);

        var materialized = agreements.ToArray();
        var canonical = new StringBuilder();
        Append(canonical, package.SourceId);
        Append(canonical, package.Id);
        canonical.Append(materialized.Length).Append(';');
        foreach (var agreement in materialized)
        {
            canonical.Append((int)agreement.Kind).Append(';');
            Append(canonical, agreement.Id);
            Append(canonical, agreement.Label);
            Append(canonical, agreement.Text);
            Append(canonical, agreement.AgreementUri?.OriginalString ?? string.Empty);
            canonical.Append(agreement.RequiresExplicitAcceptance ? '1' : '0').Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new PackageAgreementSnapshot
        {
            Package = package,
            Fingerprint = Convert.ToHexString(hash),
            Agreements = materialized
        };
    }

    public static bool IsValidFingerprint(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }
}
