/// <summary>
/// Native compatibility lane for deprecated exp-022 peer adapter task.
/// Delegates to native <c>webrtc-stack</c> in legacy controlws mode.
/// </summary>
internal static class WebRtcPeerAdapterCommand
{
    public static Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!Options.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"webrtc-peer-adapter options error: {parseError}");
            return Task.FromResult(1);
        }

        if (!options.AllowLegacyControlWs)
        {
            Console.Error.WriteLine("webrtc-peer-adapter is legacy exp-022 compatibility tooling. Pass -AllowLegacyControlWs explicitly.");
            return Task.FromResult(1);
        }

        if (!IsLegacyExp022Enabled())
        {
            Console.Error.WriteLine("Legacy exp-022 mode is disabled. Set HIDBRIDGE_ENABLE_LEGACY_EXP022=true to use webrtc-peer-adapter compatibility lane.");
            return Task.FromResult(1);
        }

        Console.WriteLine("WARNING: webrtc-peer-adapter compatibility lane is deprecated. Delegating to native webrtc-stack in controlws mode.");

        var mapped = new List<string>
        {
            "-CommandExecutor", "controlws",
            "-AllowLegacyControlWs",
            "-ApiBaseUrl", options.BaseUrl,
            "-ControlWsUrl", options.ControlWsUrl,
            "-EndpointId", options.EndpointId,
            "-PrincipalId", options.PrincipalId,
            "-TokenUsername", options.TokenUsername,
            "-TokenPassword", options.TokenPassword,
            "-KeycloakBaseUrl", options.KeycloakBaseUrl,
            "-TokenScope", options.TokenScope,
            "-UartPort", options.UartPort,
            "-UartBaud", options.UartBaud.ToString(),
            "-UartHmacKey", options.UartHmacKey,
            "-OutputJsonPath", options.OutputJsonPath,
            "-SkipIdentityReset",
            "-SkipCiLocal",
            "-SkipRuntimeBootstrap",
            "-StopExisting",
        };

        return WebRtcStackCommand.RunAsync(platformRoot, mapped);
    }

    private static bool IsLegacyExp022Enabled()
    {
        var value = Environment.GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022")
                    ?? Environment.GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022", EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022", EnvironmentVariableTarget.Machine)
                    ?? string.Empty;
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                return true;
            default:
                return false;
        }
    }

    private sealed class Options
    {
        public bool AllowLegacyControlWs { get; set; }
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string ControlWsUrl { get; set; } = "ws://127.0.0.1:18092/ws/control";
        public string EndpointId { get; set; } = "endpoint_local_demo";
        public string PrincipalId { get; set; } = "webrtc.peer.adapter";
        public string UartPort { get; set; } = "COM6";
        public int UartBaud { get; set; } = 3000000;
        public string UartHmacKey { get; set; } = "your-master-secret";
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string TokenScope { get; set; } = string.Empty;
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string OutputJsonPath { get; set; } = "Platform/.logs/webrtc-peer-adapter.result.json";

        public static bool TryParse(IReadOnlyList<string> args, out Options options, out string? error)
        {
            options = new Options();
            error = null;
            for (var i = 0; i < args.Count; i++)
            {
                var token = args[i];
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (!token.StartsWith("-", StringComparison.Ordinal))
                {
                    error = $"Unexpected token '{token}'.";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                var value = hasValue ? args[i + 1] : null;
                switch (name.ToLowerInvariant())
                {
                    case "allowlegacycontrolws":
                        options.AllowLegacyControlWs = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "controlwsurl": options.ControlWsUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "endpointid": options.EndpointId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "uartport": options.UartPort = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "uartbaud": options.UartBaud = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "uarthmackey": options.UartHmacKey = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "outputjsonpath": options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        // Keep compatibility with legacy script parameter shape.
                        // Unsupported arguments are accepted but ignored by this compatibility lane.
                        if (hasValue)
                        {
                            i++;
                        }
                        Console.WriteLine($"WARNING: webrtc-peer-adapter option '-{name}' is ignored by native compatibility lane.");
                        break;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            return true;
        }

        private static string RequireValue(string name, string? value, ref int index, ref bool hasValue, ref string? error)
        {
            if (!hasValue || string.IsNullOrWhiteSpace(value))
            {
                error = $"Option -{name} requires a value.";
                return string.Empty;
            }

            index++;
            return value;
        }

        private static bool ParseSwitch(string name, string? value, bool hasValue, ref int index, ref string? error)
        {
            if (!hasValue)
            {
                return true;
            }

            if (!TryParseBool(value, out var parsed))
            {
                error = $"Option -{name} requires true/false when value is supplied.";
                return false;
            }

            index++;
            return parsed;
        }

        private static int ParseInt(string name, string? value, bool hasValue, ref int index, ref string? error)
        {
            var raw = RequireValue(name, value, ref index, ref hasValue, ref error);
            if (error is not null)
            {
                return 0;
            }

            if (!int.TryParse(raw, out var parsed))
            {
                error = $"Option -{name} requires integer value.";
                return 0;
            }

            return parsed;
        }

        private static bool TryParseBool(string? value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    parsed = true;
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                    parsed = false;
                    return true;
                default:
                    return false;
            }
        }
    }
}
