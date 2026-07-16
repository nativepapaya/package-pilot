using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackagePilot.Core.Services;

/// <summary>
/// Versioned, length-prefixed protocol for the one-shot package-elevation helper. Its pipe
/// namespace and HMAC proof domain are intentionally distinct from source administration.
/// </summary>
public static class PackageAdminPipeProtocol
{
    public const int MaximumFrameBytes = 64 * 1024;
    public const int SecretBytes = 32;
    public const int ChallengeBytes = 32;
    public const int ProofBytes = 32;
    public const string PipeNamePrefix = "PackagePilot.PackageAdmin.";

    private static readonly byte[] ProofDomain =
        Encoding.UTF8.GetBytes("PackagePilot.PackageAdmin.v1\0");
    private static readonly JsonSerializerOptions SerializerOptions = CreateJsonOptions();

    public static JsonSerializerOptions CreateSerializerOptions() => new(SerializerOptions);

    public static string CreatePipeName() => $"{PipeNamePrefix}{Guid.NewGuid():N}";

    public static string CreateSecret()
    {
        Span<byte> secret = stackalloc byte[SecretBytes];
        try
        {
            RandomNumberGenerator.Fill(secret);
            return Convert.ToBase64String(secret)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static bool IsValidPipeName(string? value)
    {
        if (value is null
            || value.Length != PipeNamePrefix.Length + 32
            || !value.StartsWith(PipeNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return value.AsSpan(PipeNamePrefix.Length).ToString().All(Uri.IsHexDigit);
    }

    public static bool TryDecodeSecret(string? value, out byte[] secret)
    {
        secret = Array.Empty<byte>();
        if (value is null || value.Length != 43)
        {
            return false;
        }

        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/') + "=";
            var decoded = Convert.FromBase64String(normalized);
            if (decoded.Length != SecretBytes)
            {
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            secret = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static async Task AuthenticateServerAsync(
        Stream stream,
        string pipeName,
        string encodedSecret,
        CancellationToken cancellationToken = default)
    {
        ValidateHandshakeArguments(stream, pipeName, encodedSecret, out var secret);
        try
        {
            var challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
            await WriteFrameAsync(stream, challenge, cancellationToken).ConfigureAwait(false);
            var actualProof = await ReadFrameAsync(stream, ProofBytes, cancellationToken)
                .ConfigureAwait(false);
            if (actualProof.Length != ProofBytes)
            {
                CryptographicOperations.ZeroMemory(challenge);
                CryptographicOperations.ZeroMemory(actualProof);
                throw new UnauthorizedAccessException(
                    "The elevated package helper returned an invalid authentication proof.");
            }

            var expectedProof = CreateProof(secret, pipeName, challenge);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actualProof, expectedProof))
                {
                    throw new UnauthorizedAccessException(
                        "The elevated package helper did not authenticate the pipe connection.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(challenge);
                CryptographicOperations.ZeroMemory(actualProof);
                CryptographicOperations.ZeroMemory(expectedProof);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static async Task AuthenticateClientAsync(
        Stream stream,
        string pipeName,
        string encodedSecret,
        CancellationToken cancellationToken = default)
    {
        ValidateHandshakeArguments(stream, pipeName, encodedSecret, out var secret);
        try
        {
            var challenge = await ReadFrameAsync(stream, ChallengeBytes, cancellationToken)
                .ConfigureAwait(false);
            if (challenge.Length != ChallengeBytes)
            {
                CryptographicOperations.ZeroMemory(challenge);
                throw new InvalidDataException(
                    "The package broker returned an invalid authentication challenge.");
            }

            var proof = CreateProof(secret, pipeName, challenge);
            try
            {
                await WriteFrameAsync(stream, proof, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(challenge);
                CryptographicOperations.ZeroMemory(proof);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static async Task WriteJsonAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException("The package helper message is too large.");
        }

        await WriteFrameAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadJsonAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = await ReadFrameAsync(stream, MaximumFrameBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("The package helper message was empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length <= 0 || payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException("The package helper frame length is invalid.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumLength)
        {
            throw new InvalidDataException("The package helper frame length is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static byte[] CreateProof(
        ReadOnlySpan<byte> secret,
        string pipeName,
        ReadOnlySpan<byte> challenge)
    {
        var pipeBytes = Encoding.UTF8.GetBytes(pipeName);
        var material = new byte[ProofDomain.Length + pipeBytes.Length + 1 + challenge.Length];
        ProofDomain.CopyTo(material, 0);
        pipeBytes.CopyTo(material, ProofDomain.Length);
        material[ProofDomain.Length + pipeBytes.Length] = 0;
        challenge.CopyTo(material.AsSpan(ProofDomain.Length + pipeBytes.Length + 1));
        try
        {
            return HMACSHA256.HashData(secret, material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static void ValidateHandshakeArguments(
        Stream stream,
        string pipeName,
        string encodedSecret,
        out byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!IsValidPipeName(pipeName) || !TryDecodeSecret(encodedSecret, out secret))
        {
            throw new ArgumentException("The package helper pipe credentials are invalid.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
