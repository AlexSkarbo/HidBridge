using System.Text.Json;

namespace HidBridge.Edge.HidBridgeProtocol;

/// <summary>
/// Describes normalized ACK extraction result from control websocket payload.
/// </summary>
public readonly record struct ControlWsAckParseResult(bool Found, bool? IsSuccess, string ErrorMessage);

/// <summary>
/// Parses heterogeneous control-websocket ACK envelopes used by exp-022 compatible endpoints.
/// </summary>
public static class ControlWsAckParser
{
    /// <summary>
    /// Attempts to extract ACK status and error from one websocket payload root.
    /// </summary>
    public static ControlWsAckParseResult Parse(JsonElement root, string? expectedCommandId)
    {
        if (!TryFindAck(root, expectedCommandId, out var indicator, out var errorMessage))
        {
            return new ControlWsAckParseResult(false, null, string.Empty);
        }

        var status = ParseAckIndicator(indicator);
        return new ControlWsAckParseResult(true, status, errorMessage);
    }

    /// <summary>
    /// Searches nested response nodes and returns first matching ACK indicator.
    /// </summary>
    private static bool TryFindAck(JsonElement element, string? expectedId, out JsonElement? indicator, out string errorMessage)
    {
        indicator = null;
        errorMessage = string.Empty;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAck(item, expectedId, out indicator, out errorMessage))
                {
                    return true;
                }
            }

            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var payloadId = element.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        var idMatches = string.IsNullOrWhiteSpace(expectedId)
            || string.IsNullOrWhiteSpace(payloadId)
            || string.Equals(expectedId, payloadId, StringComparison.OrdinalIgnoreCase);

        if (idMatches && element.TryGetProperty("ok", out var okProperty))
        {
            indicator = okProperty;
            errorMessage = ExtractErrorText(element);
            return true;
        }

        if (idMatches && element.TryGetProperty("success", out var successProperty))
        {
            indicator = successProperty;
            errorMessage = ExtractErrorText(element);
            return true;
        }

        if (idMatches && element.TryGetProperty("status", out var statusProperty))
        {
            indicator = statusProperty;
            errorMessage = ExtractErrorText(element);
            return true;
        }

        if (idMatches)
        {
            var extractedError = ExtractErrorText(element);
            if (!string.IsNullOrWhiteSpace(extractedError))
            {
                errorMessage = extractedError;
                return true;
            }
        }

        foreach (var propertyName in new[] { "result", "payload", "data", "ack", "response", "message" })
        {
            if (element.TryGetProperty(propertyName, out var nested)
                && TryFindAck(nested, expectedId, out indicator, out errorMessage))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizes ACK indicator token to nullable success/failure state.
    /// </summary>
    private static bool? ParseAckIndicator(JsonElement? indicator)
    {
        if (!indicator.HasValue)
        {
            return null;
        }

        var value = indicator.Value;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericStatus))
        {
            return numericStatus != 0;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var token = value.GetString();
        if (bool.TryParse(token, out var parsedBoolean))
        {
            return parsedBoolean;
        }

        return TryParseStatusToken(token, out var parsedStatus)
            ? parsedStatus
            : null;
    }

    /// <summary>
    /// Normalizes textual status tokens produced by legacy peer implementations.
    /// </summary>
    private static bool TryParseStatusToken(string? token, out bool status)
    {
        status = false;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        switch (token.Trim().ToLowerInvariant())
        {
            case "ok":
            case "success":
            case "applied":
            case "accepted":
            case "done":
                status = true;
                return true;
            case "error":
            case "failed":
            case "rejected":
            case "timeout":
                status = false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Extracts best-effort human-readable error text from payload.
    /// </summary>
    private static string ExtractErrorText(JsonElement payload)
    {
        foreach (var propertyName in new[] { "error", "message", "reason", "details" })
        {
            if (payload.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }
}
