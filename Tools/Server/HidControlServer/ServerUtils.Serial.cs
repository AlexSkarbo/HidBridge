using HidControlServer.Services;

namespace HidControlServer;

// Serial port discovery and probing helpers.
/// <summary>
/// Provides server utility helpers for serial.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Executes ListSerialPortCandidates.
    /// </summary>
    /// <returns>Result.</returns>
    public static List<SerialPortInfo> ListSerialPortCandidates()
    {
        return SerialPortService.ListSerialPortCandidates();
    }

    /// <summary>
    /// Resolves serial port.
    /// </summary>
    /// <param name="configured">The configured.</param>
    /// <param name="serialAuto">The serialAuto.</param>
    /// <param name="serialMatch">The serialMatch.</param>
    /// <param name="status">The status.</param>
    /// <returns>Result.</returns>
    public static string ResolveSerialPort(string configured, bool serialAuto, string? serialMatch, out string status)
    {
        return SerialPortService.ResolveSerialPort(configured, serialAuto, serialMatch, out status);
    }

    public static bool TryResolveSerialByDeviceId(string deviceIdHex,
        int baud,
        string hmacKey,
        int injectQueueCapacity,
        int injectDropThreshold,
        out string? port,
        out string status)
    {
        return SerialPortService.TryResolveSerialByDeviceId(deviceIdHex, baud, hmacKey, injectQueueCapacity, injectDropThreshold, out port, out status);
    }

    /// <summary>
    /// Executes ProbeSerialDevices.
    /// </summary>
    /// <param name="baud">The baud.</param>
    /// <param name="hmacKey">The hmacKey.</param>
    /// <param name="injectQueueCapacity">The injectQueueCapacity.</param>
    /// <param name="injectDropThreshold">The injectDropThreshold.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <returns>Result.</returns>
    public static List<object> ProbeSerialDevices(int baud, string hmacKey, int injectQueueCapacity, int injectDropThreshold, int timeoutMs)
    {
        return SerialPortService.ProbeSerialDevices(baud, hmacKey, injectQueueCapacity, injectDropThreshold, timeoutMs);
    }
}
