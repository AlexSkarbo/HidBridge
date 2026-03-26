using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Runs UART command-path diagnostics across interface selectors and HID scenarios.
/// </summary>
internal static class UartDiagnosticsCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!Options.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"uart-diagnostics options error: {parseError}");
            return 1;
        }

        var selectors = NormalizeSelectors(options.InterfaceSelectors, options.InterfaceSelectorsCsv);
        if (selectors.Count == 0)
        {
            Console.Error.WriteLine("InterfaceSelectors did not contain any valid values between 0 and 255.");
            return 1;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var token = options.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                token = await RequestUserTokenAsync(http, options);
            }

            var headers = BuildCallerHeaders(token, options);

            var baseUrl = options.BaseUrl.TrimEnd('/');
            await SendJsonAsync(http, HttpMethod.Get, $"{baseUrl}/health", headers);
            var runtime = await SendJsonAsync(http, HttpMethod.Get, $"{baseUrl}/api/v1/runtime/uart", headers);

            var sessions = await GetSessionsAsync(http, baseUrl, headers);
            var inventory = await SendJsonAsync(http, HttpMethod.Get, $"{baseUrl}/api/v1/dashboards/inventory", headers);
            var endpoints = ReadArrayOrEmpty(inventory.RootElement, "endpoints");

            var preflightClosed = new List<string>();
            var preflightCloseFailures = new List<string>();
            var endpoint = FindReusableEndpoint(endpoints, sessions, options.PreferredEndpointId);
            var preflightCleanupAttempted = false;
            if (endpoint is null)
            {
                preflightCleanupAttempted = true;
                foreach (var session in sessions)
                {
                    var preflightSessionId = GetString(session, "sessionId");
                    var state = GetString(session, "state");
                    if (string.IsNullOrWhiteSpace(preflightSessionId) || IsTerminalState(state))
                    {
                        continue;
                    }

                    try
                    {
                        await SendJsonAsync(http, HttpMethod.Post, $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(preflightSessionId)}/close", headers, new
                        {
                            sessionId = preflightSessionId,
                            reason = "uart diagnostics preflight cleanup",
                        });
                        preflightClosed.Add(preflightSessionId);
                    }
                    catch (Exception ex)
                    {
                        preflightCloseFailures.Add($"{preflightSessionId}: {ex.Message}");
                    }
                }

                await Task.Delay(300);
                sessions = await GetSessionsAsync(http, baseUrl, headers);
                inventory = await SendJsonAsync(http, HttpMethod.Get, $"{baseUrl}/api/v1/dashboards/inventory", headers);
                endpoints = ReadArrayOrEmpty(inventory.RootElement, "endpoints");
                endpoint = FindReusableEndpoint(endpoints, sessions, options.PreferredEndpointId);
            }

            if (endpoint is null)
            {
                throw new InvalidOperationException("UART diagnostics did not find a reusable endpoint.");
            }

            var endpointId = GetString(endpoint.Value, "endpointId");
            var agentId = GetString(endpoint.Value, "agentId");
            if (string.IsNullOrWhiteSpace(endpointId) || string.IsNullOrWhiteSpace(agentId))
            {
                throw new InvalidOperationException("Selected endpoint is missing endpointId/agentId.");
            }

            var sessionId = BuildSessionId(endpointId);
            var openResponse = await SendJsonAsync(http, HttpMethod.Post, $"{baseUrl}/api/v1/sessions", headers, new
            {
                sessionId,
                profile = options.Profile,
                requestedBy = options.PrincipalId,
                targetAgentId = agentId,
                targetEndpointId = endpointId,
                shareMode = "Owner",
                tenantId = options.TenantId,
                organizationId = options.OrganizationId,
            });
            var effectivePrincipal = GetString(openResponse.RootElement, "requestedBy");
            if (string.IsNullOrWhiteSpace(effectivePrincipal))
            {
                effectivePrincipal = options.PrincipalId;
            }

            var ownerParticipantId = $"owner:{effectivePrincipal}";
            await SendJsonAsync(http, HttpMethod.Post, $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/request", headers, new
            {
                participantId = ownerParticipantId,
                requestedBy = effectivePrincipal,
                leaseSeconds = options.LeaseSeconds,
                reason = "uart diagnostics control lease",
                tenantId = options.TenantId,
                organizationId = options.OrganizationId,
            });

            var scenarios = new[]
            {
                new Scenario("keyboard.shortcut", "keyboard.shortcut"),
                new Scenario("keyboard.text", "keyboard.text"),
                new Scenario("mouse.move", "mouse.move"),
                new Scenario("keyboard.reset", "keyboard.reset"),
            };

            var probes = new List<ProbeResult>();
            try
            {
                foreach (var selector in selectors)
                {
                    foreach (var scenario in scenarios)
                    {
                        var commandId = $"cmd-uartdiag-{Guid.NewGuid():N}"[..24];
                        var commandArgs = new Dictionary<string, object?>
                        {
                            ["principalId"] = effectivePrincipal,
                            ["participantId"] = ownerParticipantId,
                            ["itfSel"] = selector,
                        };
                        switch (scenario.Name)
                        {
                            case "keyboard.shortcut":
                                commandArgs["shortcut"] = options.KeyboardShortcut;
                                commandArgs["keys"] = options.KeyboardShortcut;
                                break;
                            case "keyboard.text":
                                commandArgs["text"] = options.KeyboardText;
                                break;
                            case "mouse.move":
                                commandArgs["dx"] = options.MouseDx;
                                commandArgs["dy"] = options.MouseDy;
                                commandArgs["wheel"] = options.MouseWheel;
                                break;
                            case "keyboard.reset":
                                break;
                        }

                        string ackStatus;
                        string ackErrorCode;
                        string ackErrorMessage;
                        string journalStatus = string.Empty;
                        string journalErrorCode = string.Empty;
                        string journalErrorMessage = string.Empty;
                        try
                        {
                            var ack = await SendJsonAsync(http, HttpMethod.Post, $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/commands", headers, new
                            {
                                commandId,
                                sessionId,
                                channel = "Hid",
                                action = scenario.Action,
                                args = commandArgs,
                                timeoutMs = options.CommandTimeoutMs,
                                idempotencyKey = $"idem-uartdiag-{commandId}",
                                tenantId = options.TenantId,
                                organizationId = options.OrganizationId,
                            });
                            ackStatus = GetString(ack.RootElement, "status");
                            var ackError = GetPropertyOrNull(ack.RootElement, "error");
                            ackErrorCode = ackError is null ? string.Empty : GetString(ackError.Value, "code");
                            ackErrorMessage = ackError is null ? string.Empty : GetString(ackError.Value, "message");

                            for (var attempt = 0; attempt < 12; attempt++)
                            {
                                var journal = await SendJsonAsync(http, HttpMethod.Get, $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/commands/journal", headers);
                                var entries = ReadItems(journal.RootElement);
                                var entry = entries.FirstOrDefault(x => string.Equals(GetString(x, "commandId"), commandId, StringComparison.OrdinalIgnoreCase));
                                if (entry.ValueKind == JsonValueKind.Object)
                                {
                                    journalStatus = GetString(entry, "status");
                                    var journalError = GetPropertyOrNull(entry, "error");
                                    journalErrorCode = journalError is null ? string.Empty : GetString(journalError.Value, "code");
                                    journalErrorMessage = journalError is null ? string.Empty : GetString(journalError.Value, "message");
                                    break;
                                }

                                await Task.Delay(200);
                            }
                        }
                        catch (Exception ex)
                        {
                            ackStatus = "HttpError";
                            ackErrorCode = "HTTP_ERROR";
                            ackErrorMessage = ex.Message;
                        }

                        probes.Add(new ProbeResult(
                            selector,
                            scenario.Name,
                            scenario.Action,
                            commandId,
                            ackStatus,
                            ackErrorCode,
                            ackErrorMessage,
                            journalStatus,
                            journalErrorCode,
                            journalErrorMessage));
                    }
                }
            }
            finally
            {
                try
                {
                    await SendJsonAsync(http, HttpMethod.Post, $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/close", headers, new
                    {
                        sessionId,
                        reason = "uart diagnostics cleanup",
                    });
                }
                catch
                {
                    // best-effort cleanup
                }
            }

            var successCount = probes.Count(x => string.Equals(x.AckStatus, "Applied", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(x.AckStatus, "Accepted", StringComparison.OrdinalIgnoreCase));
            var timeoutCount = probes.Count(x => string.Equals(x.AckStatus, "Timeout", StringComparison.OrdinalIgnoreCase));
            var rejectedCount = probes.Count(x => string.Equals(x.AckStatus, "Rejected", StringComparison.OrdinalIgnoreCase));
            var httpErrorCount = probes.Count(x => string.Equals(x.AckStatus, "HttpError", StringComparison.OrdinalIgnoreCase));

            var selectorSummary = probes.GroupBy(x => x.InterfaceSelector)
                .Select(g => new
                {
                    interfaceSelector = g.Key,
                    probes = g.Count(),
                    success = g.Count(x => string.Equals(x.AckStatus, "Applied", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(x.AckStatus, "Accepted", StringComparison.OrdinalIgnoreCase)),
                    timeouts = g.Count(x => string.Equals(x.AckStatus, "Timeout", StringComparison.OrdinalIgnoreCase)),
                    rejected = g.Count(x => string.Equals(x.AckStatus, "Rejected", StringComparison.OrdinalIgnoreCase)),
                    httpErrors = g.Count(x => string.Equals(x.AckStatus, "HttpError", StringComparison.OrdinalIgnoreCase)),
                })
                .OrderByDescending(x => x.success)
                .ThenBy(x => x.interfaceSelector)
                .ToArray();

            var summary = new
            {
                baseUrl = options.BaseUrl,
                runtime = JsonSerializer.Deserialize<object>(runtime.RootElement.GetRawText()),
                readyEndpoint = endpointId,
                readyAgentId = agentId,
                sessionId,
                selectors,
                preferredEndpointId = options.PreferredEndpointId,
                scenarios = scenarios.Select(static x => x.Name).ToArray(),
                totalProbes = probes.Count,
                successProbes = successCount,
                timeoutProbes = timeoutCount,
                rejectedProbes = rejectedCount,
                httpErrorProbes = httpErrorCount,
                bestSelector = selectorSummary.FirstOrDefault()?.interfaceSelector,
                preflightCleanupAttempted,
                preflightClosedSessions = preflightClosed,
                preflightCloseFailures = preflightCloseFailures,
                selectorSummary,
                probes,
            };

            if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
            {
                var outputPath = Path.IsPathRooted(options.OutputJsonPath)
                    ? options.OutputJsonPath
                    : Path.GetFullPath(Path.Combine(platformRoot, options.OutputJsonPath));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? platformRoot);
                await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            }

            Console.WriteLine();
            Console.WriteLine("=== UART Diagnostics Summary ===");
            Console.WriteLine($"BaseUrl: {options.BaseUrl}");
            Console.WriteLine($"Runtime port/baud: {GetString(runtime.RootElement, "port")} / {GetString(runtime.RootElement, "baudRate")}");
            Console.WriteLine($"Ready endpoint: {endpointId}");
            Console.WriteLine($"Ready endpoint agent: {agentId}");
            Console.WriteLine($"Diagnostics session: {sessionId}");
            Console.WriteLine($"Selectors: {string.Join(", ", selectors)}");
            Console.WriteLine($"Total probes: {probes.Count}");
            Console.WriteLine($"Success probes: {successCount}");
            Console.WriteLine($"Timeout probes: {timeoutCount}");
            Console.WriteLine($"Rejected probes: {rejectedCount}");
            Console.WriteLine($"HTTP error probes: {httpErrorCount}");
            Console.WriteLine($"Best selector: {selectorSummary.FirstOrDefault()?.interfaceSelector}");
            if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
            {
                Console.WriteLine($"Summary JSON: {options.OutputJsonPath}");
            }

            return successCount > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<string> RequestUserTokenAsync(HttpClient http, Options options)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = options.TokenClientId,
            ["username"] = options.TokenUsername,
            ["password"] = options.TokenPassword,
        };
        if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
        {
            form["client_secret"] = options.TokenClientSecret;
        }

        if (!string.IsNullOrWhiteSpace(options.TokenScope))
        {
            form["scope"] = options.TokenScope;
        }

        var endpoint = $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{options.RealmName}/protocol/openid-connect/token";
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OIDC token request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var token) || string.IsNullOrWhiteSpace(token.GetString()))
        {
            throw new InvalidOperationException("UART diagnostics token response did not contain access_token.");
        }

        return token.GetString()!;
    }

    private static Dictionary<string, string> BuildCallerHeaders(string accessToken, Options options)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["X-HidBridge-UserId"] = options.PrincipalId,
            ["X-HidBridge-PrincipalId"] = options.PrincipalId,
            ["X-HidBridge-TenantId"] = options.TenantId,
            ["X-HidBridge-OrganizationId"] = options.OrganizationId,
            ["X-HidBridge-Role"] = "operator.admin,operator.moderator,operator.viewer",
        };
    }

    private static async Task<List<JsonElement>> GetSessionsAsync(HttpClient http, string baseUrl, IReadOnlyDictionary<string, string> headers)
    {
        var response = await SendJsonAsync(http, HttpMethod.Get, $"{baseUrl}/api/v1/sessions", headers);
        return ReadItems(response.RootElement);
    }

    private static JsonElement? FindReusableEndpoint(List<JsonElement> endpoints, List<JsonElement> sessions, string preferredEndpointId)
    {
        var ordered = endpoints.OrderByDescending(x => string.Equals(GetString(x, "endpointId"), preferredEndpointId, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var endpoint in ordered)
        {
            if (IsEndpointReusable(endpoint, sessions))
            {
                return endpoint;
            }
        }

        return null;
    }

    private static bool IsEndpointReusable(JsonElement endpoint, List<JsonElement> sessions)
    {
        var activeSessionId = GetString(endpoint, "activeSessionId");
        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            return true;
        }

        var linkedSession = sessions.FirstOrDefault(x => string.Equals(GetString(x, "sessionId"), activeSessionId, StringComparison.OrdinalIgnoreCase));
        if (linkedSession.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        return IsTerminalState(GetString(linkedSession, "state"));
    }

    private static bool IsTerminalState(string state)
    {
        return string.Equals(state, "Ended", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSessionId(string endpointId)
    {
        var normalized = endpointId.Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        var value = $"uart-diag-{normalized}-{Guid.NewGuid():N}";
        return value.Length <= 64 ? value : value[..64];
    }

    private static List<int> NormalizeSelectors(IReadOnlyList<int> selectors, string selectorsCsv)
    {
        var buffer = new List<int>();
        buffer.AddRange(selectors);
        if (!string.IsNullOrWhiteSpace(selectorsCsv))
        {
            foreach (var token in selectorsCsv.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(token, out var parsed))
                {
                    buffer.Add(parsed);
                }
            }
        }

        return buffer
            .Where(static x => x is >= 0 and <= 255)
            .Distinct()
            .OrderBy(static x => x)
            .ToList();
    }

    private static async Task<JsonDocument> SendJsonAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                if (pair.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.Value["Bearer ".Length..]);
                }

                continue;
            }

            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {method} {uri} failed: {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(content);
    }

    private static List<JsonElement> ReadItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            return [.. items.EnumerateArray().Select(static x => x.Clone())];
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return [.. root.EnumerateArray().Select(static x => x.Clone())];
        }

        return [];
    }

    private static List<JsonElement> ReadArrayOrEmpty(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. array.EnumerateArray().Select(static x => x.Clone())];
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => value.GetRawText(),
        };
    }

    private static JsonElement? GetPropertyOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value;
    }

    private readonly record struct Scenario(string Name, string Action);

    private readonly record struct ProbeResult(
        int InterfaceSelector,
        string Scenario,
        string Action,
        string CommandId,
        string AckStatus,
        string AckErrorCode,
        string AckErrorMessage,
        string JournalStatus,
        string JournalErrorCode,
        string JournalErrorMessage);

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
        public string PrincipalId { get; set; } = "uart-diagnostics";
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public string PreferredEndpointId { get; set; } = "endpoint_local_demo";
        public string Profile { get; set; } = "UltraLowLatency";
        public int LeaseSeconds { get; set; } = 60;
        public int CommandTimeoutMs { get; set; } = 500;
        public List<int> InterfaceSelectors { get; set; } = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        public string InterfaceSelectorsCsv { get; set; } = string.Empty;
        public string KeyboardShortcut { get; set; } = "CTRL+ALT+M";
        public string KeyboardText { get; set; } = "uart-diag";
        public int MouseDx { get; set; } = 8;
        public int MouseDy { get; set; } = 4;
        public int MouseWheel { get; set; }
        public string OutputJsonPath { get; set; } = string.Empty;

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
                    case "preferredendpointid": options.PreferredEndpointId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "profile": options.Profile = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "leaseseconds": options.LeaseSeconds = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "commandtimeoutms": options.CommandTimeoutMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "interfaceselectors":
                        options.InterfaceSelectors = ParseIntList(RequireValue(name, value, ref i, ref hasValue, ref error));
                        break;
                    case "interfaceselectorscsv":
                        options.InterfaceSelectorsCsv = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "keyboardshortcut": options.KeyboardShortcut = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keyboardtext": options.KeyboardText = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "mousedx": options.MouseDx = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "mousedy": options.MouseDy = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "mousewheel": options.MouseWheel = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "outputjsonpath": options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported uart-diagnostics option '{token}'.";
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

        private static List<int> ParseIntList(string input)
        {
            return input.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var value) ? value : -1)
                .Where(static x => x >= 0)
                .ToList();
        }
    }
}
