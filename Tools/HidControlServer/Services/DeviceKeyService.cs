using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HidControlServer;

namespace HidControlServer.Services;

/// <summary>
/// Manages bootstrap and derived HMAC keys for the HID device.
/// </summary>
internal static class DeviceKeyService
{
    /// <summary>
    /// Gets the bootstrap HMAC key from options.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <returns>Bootstrap key.</returns>
    public static string GetBootstrapKey(Options opt)
    {
        return string.IsNullOrWhiteSpace(opt.UartHmacKey) ? "changeme" : opt.UartHmacKey;
    }

    /// <summary>
    /// Initializes the device key and logs any failures.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task InitializeDeviceKeyAsync(Options opt, HidUartClient uart, CancellationToken ct)
    {
        try
        {
            await EnsureDeviceKeyAsync(opt, uart, ct);
        }
        catch
        {
            await Task.Delay(2000, ct);
            await EnsureDeviceKeyAsync(opt, uart, ct);
        }
    }

    /// <summary>
    /// Computes a derived HMAC key from master secret and device ID.
    /// </summary>
    /// <param name="masterSecret">Master secret.</param>
    /// <param name="deviceId">Device ID bytes.</param>
    /// <returns>Derived key bytes.</returns>
    public static byte[] ComputeDerivedKey(string masterSecret, byte[] deviceId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(masterSecret));
        return hmac.ComputeHash(deviceId);
    }

    /// <summary>
    /// Ensures a derived HMAC key is installed on the device.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task EnsureDeviceKeyAsync(Options opt, HidUartClient uart, CancellationToken ct)
    {
        byte[]? id = await uart.RequestDeviceIdAsync(1000, ct);
        if (id is null || id.Length == 0)
        {
            return;
        }
        byte[] derived = ComputeDerivedKey(opt.MasterSecret, id);
        uart.SetDerivedHmacKey(derived);
    }
}
