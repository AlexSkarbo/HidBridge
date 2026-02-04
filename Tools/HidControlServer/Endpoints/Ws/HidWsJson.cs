using System.Text.Json;
using System.Collections.Generic;

namespace HidControlServer.Endpoints.Ws;

/// <summary>
/// JSON helpers for the HID WebSocket endpoint.
/// </summary>
internal static class HidWsJson
{
    /// <summary>
    /// Gets an int from a JSON element by trying multiple property names.
    /// </summary>
    public static int GetInt(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (root.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int num)) return num;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int strNum)) return strNum;
            }
        }
        return 0;
    }

    /// <summary>
    /// Gets a nullable byte from a JSON element.
    /// </summary>
    public static byte? GetByteNullable(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int num)) return (byte)num;
        if (el.ValueKind == JsonValueKind.String && byte.TryParse(el.GetString(), out byte strNum)) return strNum;
        return null;
    }

    /// <summary>
    /// Gets a boolean from a JSON element with a fallback.
    /// </summary>
    public static bool GetBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        return fallback;
    }

    /// <summary>
    /// Gets a list of ints from an array property.
    /// </summary>
    public static List<int> GetIntArray(JsonElement root, string name)
    {
        var list = new List<int>();
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in el.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int num))
            {
                list.Add(num);
                continue;
            }
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int strNum))
            {
                list.Add(strNum);
            }
        }
        return list;
    }
}
