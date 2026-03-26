/// <summary>
/// Compatibility lane for legacy WebRTC relay smoke task.
/// Reuses native edge-agent smoke runner and preserves legacy option names.
/// </summary>
internal static class WebRtcRelaySmokeCommand
{
    public static Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!Options.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"webrtc-relay-smoke options error: {parseError}");
            return Task.FromResult(1);
        }

        var mapped = new List<string>
        {
            "-ApiBaseUrl", options.BaseUrl,
            "-KeycloakBaseUrl", options.KeycloakBaseUrl,
            "-RealmName", options.RealmName,
            "-TokenClientId", options.TokenClientId,
            "-TokenScope", options.TokenScope,
            "-TokenUsername", options.TokenUsername,
            "-TokenPassword", options.TokenPassword,
            "-PrincipalId", options.PrincipalId,
            "-TenantId", options.TenantId,
            "-OrganizationId", options.OrganizationId,
            "-TimeoutMs", Math.Max(1000, options.CommandTimeoutMs).ToString(),
            "-CommandAttempts", Math.Max(1, options.CommandPollAttempts).ToString(),
            "-CommandRetryDelayMs", Math.Max(100, options.CommandPollIntervalMs).ToString(),
            "-LeaseSeconds", Math.Max(30, options.LeaseSeconds).ToString(),
            "-OutputJsonPath", options.OutputJsonPath,
        };

        if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
        {
            mapped.Add("-TokenClientSecret");
            mapped.Add(options.TokenClientSecret);
        }

        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            Console.WriteLine("WARNING: -AccessToken is not supported by native webrtc-relay-smoke compatibility lane and will be ignored.");
        }

        return WebRtcEdgeAgentSmokeCommand.RunAsync(platformRoot, mapped);
    }

    private sealed class Options
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string RealmName { get; set; } = "hidbridge-dev";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string AccessToken { get; set; } = string.Empty;
        public string PrincipalId { get; set; } = "webrtc-relay-smoke";
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public int LeaseSeconds { get; set; } = 30;
        public int CommandTimeoutMs { get; set; } = 8000;
        public int CommandPollIntervalMs { get; set; } = 250;
        public int CommandPollAttempts { get; set; } = 30;
        public string OutputJsonPath { get; set; } = "Platform/.logs/webrtc-relay-smoke.result.json";

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
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realmname": options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tenantid": options.TenantId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "organizationid": options.OrganizationId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "leaseseconds": options.LeaseSeconds = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "commandtimeoutms": options.CommandTimeoutMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "commandpollintervalms": options.CommandPollIntervalMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "commandpollattempts": options.CommandPollAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "outputjsonpath": options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported webrtc-relay-smoke option '{token}'.";
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
    }
}
