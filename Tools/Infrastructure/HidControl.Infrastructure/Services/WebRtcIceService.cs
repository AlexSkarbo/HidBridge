using System.Security.Cryptography;
using System.Text;
using HidControl.Application.Abstractions;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Default implementation of WebRTC ICE configuration service (STUN/TURN REST).
/// </summary>
public sealed class WebRtcIceService : IWebRtcIceService
{
    private readonly IWebRtcBackend _backend;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="backend">Backend.</param>
    public WebRtcIceService(IWebRtcBackend backend)
    {
        _backend = backend;
    }

    /// <inheritdoc />
    public Task<WebRtcIceConfig> GetIceConfigAsync(CancellationToken ct)
    {
        _ = ct;
        var servers = new List<WebRtcIceServer>();

        string stun = string.IsNullOrWhiteSpace(_backend.StunUrl) ? "stun:stun.l.google.com:19302" : _backend.StunUrl.Trim();
        servers.Add(new WebRtcIceServer(new[] { stun }, username: null, credential: null, credentialType: null));

        if (_backend.TurnUrls.Count > 0 && !string.IsNullOrWhiteSpace(_backend.TurnSharedSecret))
        {
            int ttl = Math.Clamp(_backend.TurnTtlSeconds, 60, 24 * 60 * 60);
            long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl;
            string userPart = string.IsNullOrWhiteSpace(_backend.TurnUsername) ? "hidbridge" : _backend.TurnUsername.Trim();
            string username = $"{expires}:{userPart}";

            byte[] key = Encoding.UTF8.GetBytes(_backend.TurnSharedSecret);
            byte[] msg = Encoding.UTF8.GetBytes(username);
            using var hmac = new HMACSHA1(key);
            string credential = Convert.ToBase64String(hmac.ComputeHash(msg));

            servers.Add(new WebRtcIceServer(_backend.TurnUrls, username, credential, credentialType: "password"));
            return Task.FromResult(new WebRtcIceConfig(ttl, servers));
        }

        return Task.FromResult(new WebRtcIceConfig(null, servers));
    }
}

