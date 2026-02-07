using System.Security.Cryptography;
using System.Text;
using HidControl.Application.Abstractions;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Default implementation of WebRTC room id generator.
/// </summary>
public sealed class WebRtcRoomIdService : IWebRtcRoomIdService
{
    private readonly IWebRtcBackend _backend;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="backend">Backend.</param>
    public WebRtcRoomIdService(IWebRtcBackend backend)
    {
        _backend = backend;
    }

    /// <inheritdoc />
    public string GenerateControlRoomId() => GenerateRoomId(prefix: "hb-", _backend.GetDeviceIdHex());

    /// <inheritdoc />
    public string GenerateVideoRoomId() => GenerateRoomId(prefix: "hb-v-", _backend.GetDeviceIdHex());

    private static string GenerateRoomId(string prefix, string? deviceIdHex)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = new byte[6];
        RandomNumberGenerator.Fill(bytes);

        var sb = new StringBuilder(64);
        sb.Append(prefix);

        if (!string.IsNullOrWhiteSpace(deviceIdHex))
        {
            string d = deviceIdHex.Trim().ToLowerInvariant();
            if (d.Length > 8) d = d.Substring(0, 8);
            sb.Append(d);
        }
        else
        {
            sb.Append("unknown");
        }

        sb.Append('-');
        foreach (byte b in bytes)
        {
            sb.Append(alphabet[b % alphabet.Length]);
        }

        return sb.ToString();
    }
}

