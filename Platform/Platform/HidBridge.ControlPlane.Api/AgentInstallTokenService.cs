using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Signs and validates install bootstrap payloads embedded in one-time links.
/// </summary>
public sealed class AgentInstallTokenService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _signingKey;

    /// <summary>
    /// Creates one token service instance.
    /// </summary>
    public AgentInstallTokenService(AgentInstallRuntimeOptions options)
    {
        var key = options.SigningKey?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Agent install signing key must be configured.");
        }

        _signingKey = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>
    /// Creates one signed token for the provided payload.
    /// </summary>
    public string CreateToken(AgentInstallBootstrapPayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var payloadPart = Base64UrlEncode(payloadBytes);
        var signaturePart = Base64UrlEncode(ComputeSignature(payloadPart));
        return $"{payloadPart}.{signaturePart}";
    }

    /// <summary>
    /// Validates one token and returns decoded bootstrap payload.
    /// </summary>
    public bool TryValidate(string? token, out AgentInstallBootstrapPayload payload, out string error)
    {
        payload = default!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "missing_token";
            return false;
        }

        var delimiter = token.IndexOf('.');
        if (delimiter <= 0 || delimiter >= token.Length - 1)
        {
            error = "invalid_token_format";
            return false;
        }

        var payloadPart = token[..delimiter];
        var signaturePart = token[(delimiter + 1)..];

        byte[] providedSignature;
        try
        {
            providedSignature = Base64UrlDecode(signaturePart);
        }
        catch
        {
            error = "invalid_token_signature_encoding";
            return false;
        }

        var expectedSignature = ComputeSignature(payloadPart);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            error = "invalid_token_signature";
            return false;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(payloadPart);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);
            payload = JsonSerializer.Deserialize<AgentInstallBootstrapPayload>(payloadJson, JsonOptions)
                ?? throw new InvalidOperationException("Empty payload.");
        }
        catch
        {
            error = "invalid_token_payload";
            return false;
        }

        if (payload.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            error = "token_expired";
            return false;
        }

        return true;
    }

    private byte[] ComputeSignature(string payloadPart)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadPart));
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = 4 - (base64.Length % 4);
        if (padding is > 0 and < 4)
        {
            base64 = base64.PadRight(base64.Length + padding, '=');
        }

        return Convert.FromBase64String(base64);
    }
}

/// <summary>
/// Defines serialized install bootstrap payload embedded in signed install links.
/// </summary>
public sealed record AgentInstallBootstrapPayload(
    string SessionId,
    string PeerId,
    string EndpointId,
    string BaseUrl,
    string ControlPlaneWebUrl,
    string UartPort,
    string UartHmacKey,
    string FfmpegLatencyProfile,
    string MediaStreamId,
    string MediaWhipUrl,
    string MediaWhepUrl,
    string MediaPlaybackUrl,
    string MediaHealthUrl,
    string FfmpegExecutablePath,
    string FfmpegVideoDevice,
    string FfmpegAudioDevice,
    bool FfmpegUseTestSource,
    bool MediaBackendAutoStart,
    bool RequireMediaReady,
    string PackageUrl,
    string PackageSha256,
    string AgentExecutableRelativePath,
    string DefaultInstallDirectoryWindows,
    string DefaultInstallDirectoryLinux,
    DateTimeOffset ExpiresAtUtc);
