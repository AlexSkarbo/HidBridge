using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Native replacement for operator onboarding in Keycloak (group + roles + identity attributes).
/// </summary>
internal static class IdentityOnboardCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!Options.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"identity-onboard options error: {parseError}");
            return 1;
        }

        options.AdminUser = ResolveAdminUser(options.AdminUser);
        options.AdminPassword = ResolveAdminPassword(options.AdminPassword);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            var adminToken = await RequestAdminTokenAsync(http, options);
            var user = await ResolveUserAsync(http, options, adminToken);
            if (user is null)
            {
                throw new InvalidOperationException($"Operator user was not found in realm '{options.RealmName}'.");
            }

            var userId = GetRequiredString(user.Value, "id");
            var username = GetString(user.Value, "username");
            var email = GetString(user.Value, "email");
            var desiredPrincipalId = !string.IsNullOrWhiteSpace(options.PrincipalId)
                ? options.PrincipalId
                : (!string.IsNullOrWhiteSpace(username) ? username : email);

            var desiredUserAttributes = ReadAttributes(user.Value);
            if (!string.IsNullOrWhiteSpace(options.TenantId))
            {
                desiredUserAttributes["tenant_id"] = [options.TenantId];
            }

            if (!string.IsNullOrWhiteSpace(options.OrganizationId))
            {
                desiredUserAttributes["org_id"] = [options.OrganizationId];
            }

            if (!string.IsNullOrWhiteSpace(desiredPrincipalId))
            {
                desiredUserAttributes["principal_id"] = [desiredPrincipalId];
            }

            var currentUserAttributes = ReadAttributes(user.Value);
            var changedUserAttributeKeys = DiffAttributeKeys(currentUserAttributes, desiredUserAttributes);
            var userAttributesNeedUpdate = changedUserAttributeKeys.Count > 0;

            var group = await FindGroupByNameAsync(http, options, adminToken, options.GroupName);
            var groupExists = group is not null;
            var groupId = groupExists ? GetRequiredString(group!.Value, "id") : string.Empty;
            var currentGroupAttributes = groupExists ? ReadAttributes(group!.Value) : new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var desiredGroupAttributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(options.TenantId))
            {
                desiredGroupAttributes["tenant_id"] = [options.TenantId];
            }

            if (!string.IsNullOrWhiteSpace(options.OrganizationId))
            {
                desiredGroupAttributes["org_id"] = [options.OrganizationId];
            }

            var groupAttributesNeedUpdate = !groupExists || !AttributesEqual(currentGroupAttributes, desiredGroupAttributes);
            var missingGroupRoles = new List<string>();
            if (!groupExists)
            {
                missingGroupRoles.AddRange(options.RequiredRealmRoles);
            }
            else
            {
                var existingRoleNames = await GetGroupRoleNamesAsync(http, options, adminToken, groupId);
                foreach (var role in options.RequiredRealmRoles)
                {
                    if (!existingRoleNames.Contains(role, StringComparer.Ordinal))
                    {
                        missingGroupRoles.Add(role);
                    }
                }
            }

            var userInGroup = false;
            if (groupExists)
            {
                userInGroup = await TestUserInGroupAsync(http, options, adminToken, userId, groupId);
            }

            var plan = new
            {
                createGroup = !groupExists,
                updateGroupAttributes = groupAttributesNeedUpdate,
                assignGroupRealmRoles = missingGroupRoles,
                updateUserAttributes = userAttributesNeedUpdate,
                changedUserAttributeKeys,
                addUserToGroup = !userInGroup,
            };

            var applied = new AppliedState();
            if (!options.PrintOnly)
            {
                if (!groupExists)
                {
                    await CreateGroupAsync(http, options, adminToken, options.GroupName, desiredGroupAttributes);
                    applied.GroupCreated = true;
                    group = await FindGroupByNameAsync(http, options, adminToken, options.GroupName);
                    if (group is null)
                    {
                        throw new InvalidOperationException($"Failed to resolve group '{options.GroupName}' after create.");
                    }

                    groupId = GetRequiredString(group.Value, "id");
                }

                if (groupAttributesNeedUpdate)
                {
                    await UpdateGroupAttributesAsync(http, options, adminToken, groupId, options.GroupName, desiredGroupAttributes);
                    applied.GroupAttributesUpdated = true;
                }

                if (missingGroupRoles.Count > 0)
                {
                    var assigned = await AssignGroupRolesAsync(http, options, adminToken, groupId, missingGroupRoles);
                    applied.GroupRolesAssigned.AddRange(assigned);
                }

                if (userAttributesNeedUpdate)
                {
                    await UpdateUserAttributesAsync(http, options, adminToken, userId, desiredUserAttributes);
                    applied.UserAttributesUpdated = true;
                }

                if (!userInGroup)
                {
                    await AddUserToGroupAsync(http, options, adminToken, userId, groupId);
                    applied.UserAddedToGroup = true;
                }
            }

            var summary = new
            {
                contract = new
                {
                    realm = options.RealmName,
                    user = new { id = userId, username, email },
                    identityDefaults = new
                    {
                        tenant_id = options.TenantId,
                        org_id = options.OrganizationId,
                        principal_id = desiredPrincipalId,
                    },
                    groupAssignment = new
                    {
                        name = options.GroupName,
                        requiredRealmRoles = options.RequiredRealmRoles,
                    },
                },
                plan,
                mode = new { printOnly = options.PrintOnly, whatIf = false },
                applied = new
                {
                    groupCreated = applied.GroupCreated,
                    groupAttributesUpdated = applied.GroupAttributesUpdated,
                    groupRolesAssigned = applied.GroupRolesAssigned,
                    userAttributesUpdated = applied.UserAttributesUpdated,
                    userAddedToGroup = applied.UserAddedToGroup,
                },
            };

            Console.WriteLine();
            Console.WriteLine("=== Operator Onboarding Summary ===");
            Console.WriteLine($"Realm: {options.RealmName}");
            Console.WriteLine($"User: {username} ({userId})");
            Console.WriteLine($"Group: {options.GroupName}");
            Console.WriteLine($"Required roles: {string.Join(", ", options.RequiredRealmRoles)}");
            Console.WriteLine($"Planned group create: {plan.createGroup}");
            Console.WriteLine($"Planned group attribute update: {plan.updateGroupAttributes}");
            Console.WriteLine($"Planned missing group roles: {string.Join(", ", plan.assignGroupRealmRoles)}");
            Console.WriteLine($"Planned user attribute update: {plan.updateUserAttributes}");
            Console.WriteLine($"Planned group membership add: {plan.addUserToGroup}");
            Console.WriteLine($"Applied group create: {applied.GroupCreated}");
            Console.WriteLine($"Applied group attribute update: {applied.GroupAttributesUpdated}");
            Console.WriteLine($"Applied group roles: {string.Join(", ", applied.GroupRolesAssigned)}");
            Console.WriteLine($"Applied user attribute update: {applied.UserAttributesUpdated}");
            Console.WriteLine($"Applied group membership add: {applied.UserAddedToGroup}");

            if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
            {
                var outputPath = Path.IsPathRooted(options.OutputJsonPath)
                    ? options.OutputJsonPath
                    : Path.GetFullPath(Path.Combine(platformRoot, options.OutputJsonPath));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? platformRoot);
                await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Summary JSON: {outputPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ResolveAdminUser(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Environment.GetEnvironmentVariable("HIDBRIDGE_KEYCLOAK_ADMIN_USER")
               ?? Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN")
               ?? "admin";
    }

    private static string ResolveAdminPassword(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Environment.GetEnvironmentVariable("HIDBRIDGE_KEYCLOAK_ADMIN_PASSWORD")
               ?? Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN_PASSWORD")
               ?? "1q-p2w0o";
    }

    private static async Task<string> RequestAdminTokenAsync(HttpClient http, Options options)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = options.AdminUser,
            ["password"] = options.AdminPassword,
        };

        var endpoint = $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{options.AdminRealm}/protocol/openid-connect/token";
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Keycloak admin authentication failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var tokenElement) || string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new InvalidOperationException("Keycloak admin token response did not contain access_token.");
        }

        return tokenElement.GetString()!;
    }

    private static async Task<JsonElement?> ResolveUserAsync(HttpClient http, Options options, string adminToken)
    {
        if (!string.IsNullOrWhiteSpace(options.UserId))
        {
            var byId = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Get, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/users/{Uri.EscapeDataString(options.UserId)}");
            if (byId.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            EnsureSuccess(byId, "Resolve user by id");
            using var doc = JsonDocument.Parse(byId.Body);
            return doc.RootElement.Clone();
        }

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var exact = await FindUsersAsync(http, options, adminToken, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/users?username={Uri.EscapeDataString(options.Username)}&exact=true");
            var user = exact.FirstOrDefault(static x => string.Equals(GetString(x, "username"), x.GetProperty("username").GetString(), StringComparison.OrdinalIgnoreCase));
            if (user.ValueKind != JsonValueKind.Undefined)
            {
                return user.Clone();
            }

            var search = await FindUsersAsync(http, options, adminToken, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/users?search={Uri.EscapeDataString(options.Username)}");
            user = search.FirstOrDefault(static x => string.Equals(GetString(x, "username"), x.GetProperty("username").GetString(), StringComparison.OrdinalIgnoreCase));
            if (user.ValueKind != JsonValueKind.Undefined)
            {
                return user.Clone();
            }

            return null;
        }

        var byEmail = await FindUsersAsync(http, options, adminToken, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/users?email={Uri.EscapeDataString(options.Email)}");
        var emailUser = byEmail.FirstOrDefault(x => string.Equals(GetString(x, "email"), options.Email, StringComparison.OrdinalIgnoreCase));
        if (emailUser.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return emailUser.Clone();
    }

    private static async Task<List<JsonElement>> FindUsersAsync(HttpClient http, Options options, string adminToken, string path)
    {
        var result = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Get, path);
        EnsureSuccess(result, "Find users");
        using var doc = JsonDocument.Parse(result.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. doc.RootElement.EnumerateArray().Select(static x => x.Clone())];
    }

    private static async Task<JsonElement?> FindGroupByNameAsync(HttpClient http, Options options, string adminToken, string groupName)
    {
        var response = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Get, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/groups?search={Uri.EscapeDataString(groupName)}");
        EnsureSuccess(response, "Find group");
        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (string.Equals(GetString(item, "name"), groupName, StringComparison.Ordinal))
            {
                return item.Clone();
            }
        }

        return null;
    }

    private static async Task CreateGroupAsync(HttpClient http, Options options, string adminToken, string groupName, IReadOnlyDictionary<string, IReadOnlyList<string>> attrs)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = groupName,
            ["attributes"] = attrs.ToDictionary(static x => x.Key, static x => x.Value.ToArray()),
        };
        var response = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Post, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/groups", body);
        EnsureSuccess(response, "Create group");
    }

    private static async Task UpdateGroupAttributesAsync(HttpClient http, Options options, string adminToken, string groupId, string groupName, IReadOnlyDictionary<string, IReadOnlyList<string>> attrs)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = groupName,
            ["attributes"] = attrs.ToDictionary(static x => x.Key, static x => x.Value.ToArray()),
        };
        var response = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Put, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/groups/{Uri.EscapeDataString(groupId)}", body);
        EnsureSuccess(response, "Update group attributes");
    }

    private static async Task<HashSet<string>> GetGroupRoleNamesAsync(HttpClient http, Options options, string adminToken, string groupId)
    {
        var response = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Get, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/groups/{Uri.EscapeDataString(groupId)}/role-mappings/realm");
        EnsureSuccess(response, "Get group role mappings");
        using var doc = JsonDocument.Parse(response.Body);
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var name = GetString(item, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static async Task<List<string>> AssignGroupRolesAsync(HttpClient http, Options options, string adminToken, string groupId, IReadOnlyList<string> missingRoles)
    {
        var roleReps = new List<JsonElement>();
        foreach (var role in missingRoles)
        {
            var roleResponse = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Get, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/roles/{Uri.EscapeDataString(role)}");
            EnsureSuccess(roleResponse, $"Resolve realm role '{role}'");
            using var roleDoc = JsonDocument.Parse(roleResponse.Body);
            roleReps.Add(roleDoc.RootElement.Clone());
        }

        if (roleReps.Count == 0)
        {
            return [];
        }

        var payload = roleReps.Select(static x => JsonSerializer.Deserialize<object>(x.GetRawText())!).ToArray();
        var assignResponse = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Post, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/groups/{Uri.EscapeDataString(groupId)}/role-mappings/realm", payload);
        EnsureSuccess(assignResponse, "Assign group realm roles");
        return [.. missingRoles];
    }

    private static async Task UpdateUserAttributesAsync(HttpClient http, Options options, string adminToken, string userId, IReadOnlyDictionary<string, IReadOnlyList<string>> attrs)
    {
        var payload = new Dictionary<string, object?>
        {
            ["attributes"] = attrs.ToDictionary(static x => x.Key, static x => x.Value.ToArray()),
        };
        var response = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Put, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/users/{Uri.EscapeDataString(userId)}", payload);
        EnsureSuccess(response, "Update user attributes");
    }

    private static async Task<bool> TestUserInGroupAsync(HttpClient http, Options options, string adminToken, string userId, string groupId)
    {
        var response = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Get, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/users/{Uri.EscapeDataString(userId)}/groups");
        EnsureSuccess(response, "Resolve user groups");
        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (string.Equals(GetString(item, "id"), groupId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task AddUserToGroupAsync(HttpClient http, Options options, string adminToken, string userId, string groupId)
    {
        var response = await SendAdminRequestAsync(http, options, adminToken, HttpMethod.Put, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/users/{Uri.EscapeDataString(userId)}/groups/{Uri.EscapeDataString(groupId)}");
        EnsureSuccess(response, "Add user to group");
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadAttributes(JsonElement element)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!element.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in attrs.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var values = property.Value.EnumerateArray()
                    .Select(static x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())
                    .Where(static x => !string.IsNullOrWhiteSpace(x))
                    .Select(static x => x!)
                    .ToArray();
                result[property.Name] = values;
                continue;
            }

            var scalar = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.GetRawText();
            result[property.Name] = string.IsNullOrWhiteSpace(scalar) ? [] : [scalar!];
        }

        return result;
    }

    private static List<string> DiffAttributeKeys(
        IReadOnlyDictionary<string, IReadOnlyList<string>> left,
        IReadOnlyDictionary<string, IReadOnlyList<string>> right)
    {
        var keys = new HashSet<string>(left.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(right.Keys);
        var changed = new List<string>();
        foreach (var key in keys)
        {
            var l = left.TryGetValue(key, out var lValues) ? NormalizeValues(lValues) : [];
            var r = right.TryGetValue(key, out var rValues) ? NormalizeValues(rValues) : [];
            if (!l.SequenceEqual(r, StringComparer.Ordinal))
            {
                changed.Add(key);
            }
        }

        changed.Sort(StringComparer.OrdinalIgnoreCase);
        return changed;
    }

    private static bool AttributesEqual(
        IReadOnlyDictionary<string, IReadOnlyList<string>> left,
        IReadOnlyDictionary<string, IReadOnlyList<string>> right)
    {
        return DiffAttributeKeys(left, right).Count == 0;
    }

    private static string[] NormalizeValues(IReadOnlyList<string> values)
    {
        return values
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetRequiredString(JsonElement element, string property)
    {
        var value = GetString(element, property);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Keycloak payload is missing required property '{property}'.");
        }

        return value;
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
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

    private static void EnsureSuccess(AdminResponse response, string operation)
    {
        if ((int)response.StatusCode is >= 200 and < 300)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed with {(int)response.StatusCode}: {response.Body}");
    }

    private static async Task<AdminResponse> SendAdminRequestAsync(
        HttpClient http,
        Options options,
        string token,
        HttpMethod method,
        string relativePath,
        object? payload = null)
    {
        var url = $"{options.KeycloakBaseUrl.TrimEnd('/')}{relativePath}";
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return new AdminResponse(response.StatusCode, body);
    }

    private readonly record struct AdminResponse(HttpStatusCode StatusCode, string Body);

    private sealed class AppliedState
    {
        public bool GroupCreated { get; set; }
        public bool GroupAttributesUpdated { get; set; }
        public List<string> GroupRolesAssigned { get; } = [];
        public bool UserAttributesUpdated { get; set; }
        public bool UserAddedToGroup { get; set; }
    }

    private sealed class Options
    {
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string AdminRealm { get; set; } = "master";
        public string AdminUser { get; set; } = string.Empty;
        public string AdminPassword { get; set; } = string.Empty;
        public string RealmName { get; set; } = "hidbridge-dev";
        public string UserId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string GroupName { get; set; } = "hidbridge-operators";
        public List<string> RequiredRealmRoles { get; set; } = ["operator.viewer"];
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public string PrincipalId { get; set; } = string.Empty;
        public bool PrintOnly { get; set; }
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
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminrealm": options.AdminRealm = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminuser": options.AdminUser = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminpassword": options.AdminPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realmname": options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "userid": options.UserId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "username": options.Username = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "email": options.Email = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "groupname": options.GroupName = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "requiredrealmroles":
                        options.RequiredRealmRoles = ParseCsv(RequireValue(name, value, ref i, ref hasValue, ref error));
                        break;
                    case "tenantid": options.TenantId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "organizationid": options.OrganizationId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "printonly":
                    case "dryrun":
                        options.PrintOnly = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "outputjsonpath": options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported identity-onboard option '{token}'.";
                        break;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(options.UserId)
                && string.IsNullOrWhiteSpace(options.Username)
                && string.IsNullOrWhiteSpace(options.Email))
            {
                error = "Specify one of -UserId, -Username, or -Email.";
                return false;
            }

            options.RequiredRealmRoles = options.RequiredRealmRoles
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (options.RequiredRealmRoles.Count == 0)
            {
                options.RequiredRealmRoles = ["operator.viewer"];
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

        private static List<string> ParseCsv(string csv)
        {
            return csv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
    }
}
