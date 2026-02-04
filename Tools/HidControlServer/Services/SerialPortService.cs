using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using HidControl.Contracts;

namespace HidControlServer.Services;

/// <summary>
/// Discovers serial ports and resolves devices by identity.
/// </summary>
internal static class SerialPortService
{
    /// <summary>
    /// Lists serial port candidates with metadata.
    /// </summary>
    /// <returns>Port list.</returns>
    public static List<SerialPortInfo> ListSerialPortCandidates()
    {
        var list = new List<SerialPortInfo>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? name = obj["Name"] as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string? deviceId = obj["DeviceID"] as string;
                    string? pnpDeviceId = obj["PNPDeviceID"] as string;
                    string? service = obj["Service"] as string;
                    Match m = Regex.Match(name, @"\((COM\d+)\)", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    string port = m.Groups[1].Value;
                    list.Add(new SerialPortInfo(port, name, deviceId, pnpDeviceId, service));
                }
            }
            catch
            {
                // ignore and fallback to SerialPort.GetPortNames
            }
        }

        if (list.Count == 0)
        {
            foreach (string port in SerialPort.GetPortNames())
            {
                list.Add(new SerialPortInfo(port, port, null, null, null));
            }
        }

        return list;
    }

    /// <summary>
    /// Resolves serial port with auto selection and matching.
    /// </summary>
    /// <param name="configured">Configured port.</param>
    /// <param name="serialAuto">Auto-select flag.</param>
    /// <param name="serialMatch">Match criteria.</param>
    /// <param name="status">Resolution status.</param>
    /// <returns>Resolved port.</returns>
    public static string ResolveSerialPort(string configured, bool serialAuto, string? serialMatch, out string status)
    {
        status = serialAuto ? "auto_enabled" : "disabled";
        if (!serialAuto && !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        var candidates = ListSerialPortCandidates();
        if (!string.IsNullOrWhiteSpace(serialMatch))
        {
            if (serialMatch.StartsWith("device:", StringComparison.OrdinalIgnoreCase) ||
                serialMatch.StartsWith("deviceid:", StringComparison.OrdinalIgnoreCase))
            {
                status = "deviceid_pending";
                return configured;
            }
            foreach (var c in candidates)
            {
                if ((c.PnpDeviceId?.IndexOf(serialMatch, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                    (c.DeviceId?.IndexOf(serialMatch, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                    (c.Name?.IndexOf(serialMatch, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                {
                    status = "matched";
                    return c.Port;
                }
            }
            status = "match_not_found";
        }

        if (candidates.Count == 1)
        {
            status = "single";
            return candidates[0].Port;
        }

        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (candidates.Any(c => string.Equals(c.Port, configured, StringComparison.OrdinalIgnoreCase)))
            {
                status = "configured_found";
                return configured;
            }
        }

        if (candidates.Count > 0)
        {
            status = "first";
            return candidates[0].Port;
        }

        status = "none";
        return configured;
    }

    /// <summary>
    /// Tries to resolve a serial port by device ID.
    /// </summary>
    public static bool TryResolveSerialByDeviceId(string deviceIdHex,
        int baud,
        string hmacKey,
        int injectQueueCapacity,
        int injectDropThreshold,
        out string? port,
        out string status)
    {
        port = null;
        status = "deviceid_not_found";
        string hex = deviceIdHex.Trim();
        if (hex.StartsWith("device:", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring("device:".Length);
        }
        if (hex.StartsWith("deviceid:", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring("deviceid:".Length);
        }
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring(2);
        }
        hex = hex.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            status = "deviceid_invalid";
            return false;
        }

        byte[] target;
        try
        {
            target = Convert.FromHexString(hex);
        }
        catch
        {
            status = "deviceid_invalid";
            return false;
        }

        foreach (var cand in ListSerialPortCandidates())
        {
            try
            {
                var client = new HidUartClient(cand.Port, baud, hmacKey, injectQueueCapacity, injectDropThreshold);
                byte[]? id;
                try
                {
                    id = client.RequestDeviceIdAsync(600, CancellationToken.None).GetAwaiter().GetResult();
                }
                finally
                {
                    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                if (id is null || id.Length == 0) continue;
                if (id.AsSpan().SequenceEqual(target))
                {
                    port = cand.Port;
                    status = "deviceid_matched";
                    return true;
                }
            }
            catch
            {
                // ignore probe errors
            }
        }

        return false;
    }

    /// <summary>
    /// Probes serial devices and returns results.
    /// </summary>
    public static List<object> ProbeSerialDevices(int baud, string hmacKey, int injectQueueCapacity, int injectDropThreshold, int timeoutMs)
    {
        var results = new List<object>();
        foreach (var cand in ListSerialPortCandidates())
        {
            try
            {
                var client = new HidUartClient(cand.Port, baud, hmacKey, injectQueueCapacity, injectDropThreshold);
                byte[]? id;
                try
                {
                    id = client.RequestDeviceIdAsync(timeoutMs, CancellationToken.None).GetAwaiter().GetResult();
                }
                finally
                {
                    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                if (id is null || id.Length == 0) continue;
                string hex = Convert.ToHexString(id);
                results.Add(new { port = cand.Port, deviceId = "0x" + hex });
            }
            catch
            {
                // ignore probe errors
            }
        }
        return results;
    }
}
