using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitlabMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GitlabMCPSharp.Tools;

/// <summary>
/// Creating projects, sharing them with groups, and reading back who a project is shared with —
/// the operations an agent needs to stand up a new repository, as opposed to working inside one
/// that already exists.
///
/// The write tools are deliberately conservative. Creation defaults to private and always sends
/// visibility explicitly rather than inheriting whatever the instance default happens to be;
/// sharing never removes or downgrades an existing share. Neither has a counterpart that deletes a
/// project or revokes a share, so the worst case is an extra project or an extra group link.
/// </summary>
[McpServerToolType]
public static class ProjectProvisioningTools
{
    /// <summary>
    /// Access levels GitLab accepts on <c>POST /projects/:id/share</c>, taken from its published
    /// OpenAPI schema (<c>group_access</c> enum: 10, 15, 20, 25, 30, 40, 50).
    ///
    /// Note 5 (Minimal access) is a valid *membership* level but is NOT accepted here, so it is
    /// absent on purpose — offering it would produce a confusing 400 from GitLab.
    /// </summary>
    private static readonly Dictionary<string, int> ShareAccessLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["guest"] = 10,
        ["planner"] = 15,
        ["reporter"] = 20,
        ["security_manager"] = 25,
        ["developer"] = 30,
        ["maintainer"] = 40,
        ["owner"] = 50,
    };

    private static string DescribeAccessLevel(int level) => level switch
    {
        0 => "no_access",
        5 => "minimal",
        10 => "guest",
        15 => "planner",
        20 => "reporter",
        25 => "security_manager",
        30 => "developer",
        40 => "maintainer",
        50 => "owner",
        _ => $"unknown({level})",
    };

    // ---------------------------------------------------------------- create

    [McpServerTool(Name = "gl_create_project"),
     Description("Create a new GitLab project. Defaults to private visibility. If a project already exists at the same namespace/path it is returned unchanged rather than modified, so this is safe to retry. Requires write mode.")]
    public static async Task<string> CreateProject(
        GitlabService svc,
        [Description("Project name, e.g. 'LoopWeaver'.")] string name,
        [Description("URL path slug. Defaults to the name when omitted.")] string? path = null,
        [Description("Full namespace path to create in, e.g. 'my-group' or 'my-group/sub'. Omit to create in the authenticated user's personal namespace.")] string? @namespace = null,
        [Description("Short project description.")] string? description = null,
        [Description("Visibility: private (default), internal, or public.")] string visibility = "private",
        [Description("Have GitLab create an initial commit with a README. Leave false if you intend to push your own root commit.")] bool initializeWithReadme = false,
        CancellationToken cancellationToken = default)
    {
        const string Tool = "gl_create_project";

        if (string.IsNullOrWhiteSpace(name))
            throw new McpException($"{Tool}: a project name is required.");

        svc.EnsureWriteAllowed("create_project");

        var slug = string.IsNullOrWhiteSpace(path) ? name.Trim() : path.Trim();
        var visibilityValue = NormalizeVisibility(visibility, Tool);

        // Resolve the namespace to a concrete id before doing anything else. A wrong namespace
        // silently creates the project somewhere unintended, which is not recoverable by this
        // server — there is no delete tool.
        long? namespaceId = null;
        string? namespaceFullPath = null;
        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            var requested = @namespace.Trim().Trim('/');
            (namespaceId, namespaceFullPath) = await ResolveNamespaceAsync(svc, requested, Tool, cancellationToken);

            // A group namespace is subject to the group allow-list; a personal namespace is not,
            // because it is the authenticated user's own space.
            if (!string.Equals(namespaceFullPath, requested, StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException(
                    $"{Tool}: namespace '{requested}' resolved to '{namespaceFullPath}', which is not an exact match. " +
                    "Refusing to create the project somewhere other than the namespace you named.");
            }
        }

        var expectedPathWithNamespace = namespaceFullPath is null ? slug : $"{namespaceFullPath}/{slug}";
        svc.EnsureProjectPathAllowed(expectedPathWithNamespace);

        // Idempotency: if it is already there, hand it back untouched.
        var existing = await TryGetProjectAsync(svc, expectedPathWithNamespace, Tool, cancellationToken);
        if (existing is not null)
        {
            return JsonSerializer.Serialize(new
            {
                Result = "AlreadyExists",
                Created = false,
                Message = $"A project already exists at '{expectedPathWithNamespace}'. It was returned unchanged.",
                Project = SummariseProject(existing),
            }, JsonOpts.Default);
        }

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name.Trim(),
            ["path"] = slug,
            // Always explicit — never inherit the instance default.
            ["visibility"] = visibilityValue,
            ["initialize_with_readme"] = initializeWithReadme,
        };
        if (namespaceId is not null) payload["namespace_id"] = namespaceId.Value;
        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description;

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await svc.Http.PostAsync("projects", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new McpException(DescribeFailure(Tool, response.StatusCode, body,
                $"creating project '{expectedPathWithNamespace}'", response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "GitLab rejected the request — usually an invalid path slug or a name that collides within the namespace.",
                    HttpStatusCode.Forbidden => "The token is valid but lacks permission to create a project in that namespace.",
                    HttpStatusCode.NotFound => "The namespace was not found, or the token cannot see it.",
                    HttpStatusCode.Conflict => "A project already exists at that path.",
                    _ => null,
                }));
        }

        var created = JsonNode.Parse(body) as JsonObject
            ?? throw new McpException($"{Tool}: GitLab returned an unexpected response shape.");

        return JsonSerializer.Serialize(new
        {
            Result = "Created",
            Created = true,
            Project = SummariseProject(created),
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- share

    [McpServerTool(Name = "gl_share_project_with_group"),
     Description("Share a project with a GitLab group at a given access level. Idempotent: if the group already has that level or higher, nothing changes. Never downgrades or replaces an existing share — a lower existing level is reported as a conflict instead. Requires write mode.")]
    public static async Task<string> ShareProjectWithGroup(
        GitlabService svc,
        [Description("Project ID or exact namespaced path, e.g. 'my-group/my-project'.")] string project,
        [Description("Group ID or exact full path, e.g. 'agents' or 'parent/agents'.")] string group,
        [Description("Access level for the group: guest, planner, reporter, security_manager, developer, maintainer, owner.")] string accessLevel,
        [Description("Optional expiry for the share, as an ISO date (YYYY-MM-DD).")] string? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        const string Tool = "gl_share_project_with_group";

        svc.EnsureWriteAllowed("share_project_with_group");

        if (string.IsNullOrWhiteSpace(project))
            throw new McpException($"{Tool}: a project is required.");
        if (string.IsNullOrWhiteSpace(group))
            throw new McpException($"{Tool}: a group is required.");

        if (!ShareAccessLevels.TryGetValue(accessLevel?.Trim() ?? string.Empty, out var requestedLevel))
        {
            throw new McpException(
                $"{Tool}: '{accessLevel}' is not a valid access level for a project share. " +
                $"Use one of: {string.Join(", ", ShareAccessLevels.Keys)}. " +
                "(GitLab does not accept 'minimal' on this endpoint, even though it is a valid membership level.)");
        }

        var projectPath = svc.ResolveProject(project);
        var groupPath = svc.EnsureGroupAllowed(group.Trim().Trim('/'));

        var (groupId, groupFullPath, groupName) = await ResolveGroupAsync(svc, groupPath, Tool, cancellationToken);

        var projectObj = await TryGetProjectAsync(svc, projectPath, Tool, cancellationToken)
            ?? throw new McpException(
                $"{Tool}: project '{projectPath}' was not found, or the token cannot see it.");

        // Read the current shares before mutating, so an existing link is never clobbered.
        var current = FindExistingShare(projectObj, groupId);
        if (current is not null)
        {
            var currentLevel = current.Value.AccessLevel;

            if (currentLevel >= requestedLevel)
            {
                return JsonSerializer.Serialize(new
                {
                    Result = "AlreadyShared",
                    Changed = false,
                    Message = currentLevel == requestedLevel
                        ? $"'{groupFullPath}' already has {DescribeAccessLevel(currentLevel)} ({currentLevel}) on '{projectPath}'."
                        : $"'{groupFullPath}' already has {DescribeAccessLevel(currentLevel)} ({currentLevel}) on '{projectPath}', which is higher than the requested {DescribeAccessLevel(requestedLevel)} ({requestedLevel}). Left unchanged rather than downgraded.",
                    Project = new { Path = projectPath, Id = projectObj["id"]?.GetValue<long>() },
                    Group = new { Id = groupId, Path = groupFullPath, Name = groupName },
                    RequestedAccess = new { Level = requestedLevel, Name = DescribeAccessLevel(requestedLevel) },
                    EffectiveAccess = new { Level = currentLevel, Name = DescribeAccessLevel(currentLevel) },
                    ExpiresAt = current.Value.ExpiresAt,
                }, JsonOpts.Default);
            }

            // Raising the level would mean delete-then-recreate. GitLab's API has no in-place
            // update — its OpenAPI schema exposes only POST /projects/:id/share and
            // DELETE /projects/:id/share/:group_id — and this server has no unshare tool by
            // design, so report the conflict rather than destroying the existing link.
            return JsonSerializer.Serialize(new
            {
                Result = "Conflict",
                Changed = false,
                Message = $"'{groupFullPath}' already has {DescribeAccessLevel(currentLevel)} ({currentLevel}) on '{projectPath}', " +
                          $"which is lower than the requested {DescribeAccessLevel(requestedLevel)} ({requestedLevel}). " +
                          "GitLab has no in-place share update, so raising it requires removing the existing share and re-adding it. " +
                          "This server does not remove shares — do it in the GitLab UI or with an explicit unshare call.",
                Project = new { Path = projectPath, Id = projectObj["id"]?.GetValue<long>() },
                Group = new { Id = groupId, Path = groupFullPath, Name = groupName },
                RequestedAccess = new { Level = requestedLevel, Name = DescribeAccessLevel(requestedLevel) },
                EffectiveAccess = new { Level = currentLevel, Name = DescribeAccessLevel(currentLevel) },
                ExpiresAt = current.Value.ExpiresAt,
            }, JsonOpts.Default);
        }

        var payload = new Dictionary<string, object?>
        {
            ["group_id"] = groupId,
            ["group_access"] = requestedLevel,
        };
        if (!string.IsNullOrWhiteSpace(expiresAt)) payload["expires_at"] = expiresAt.Trim();

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await svc.Http.PostAsync(
            $"projects/{Uri.EscapeDataString(projectPath)}/share", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new McpException(DescribeFailure(Tool, response.StatusCode, body,
                $"sharing '{projectPath}' with group '{groupFullPath}'", response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "GitLab rejected the share — the group may already be linked, or the instance/group may restrict sharing.",
                    HttpStatusCode.Forbidden => "The token is valid but lacks permission to share this project, or group sharing is disabled for the namespace.",
                    HttpStatusCode.NotFound => "The project or the group was not found, or the token cannot see it.",
                    _ => null,
                }));
        }

        var link = JsonNode.Parse(body) as JsonObject;
        var grantedLevel = link?["group_access"]?.GetValue<int>() ?? requestedLevel;

        return JsonSerializer.Serialize(new
        {
            Result = "Shared",
            Changed = true,
            Project = new { Path = projectPath, Id = projectObj["id"]?.GetValue<long>() },
            Group = new { Id = groupId, Path = groupFullPath, Name = groupName },
            RequestedAccess = new { Level = requestedLevel, Name = DescribeAccessLevel(requestedLevel) },
            EffectiveAccess = new { Level = grantedLevel, Name = DescribeAccessLevel(grantedLevel) },
            ExpiresAt = link?["expires_at"]?.GetValue<string?>(),
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- verify

    [McpServerTool(Name = "gl_list_project_group_shares"),
     Description("List the groups a project is shared with, and each group's access level. Read-only. Use this to verify a share actually landed — it reads the project's live state rather than trusting a previous write's response.")]
    public static async Task<string> ListProjectGroupShares(
        GitlabService svc,
        [Description("Project ID or exact namespaced path, e.g. 'my-group/my-project'.")] string? project = null,
        [Description("Only report this group, matched exactly on full path or numeric id. Omit to list every share.")] string? group = null,
        CancellationToken cancellationToken = default)
    {
        const string Tool = "gl_list_project_group_shares";

        var projectPath = svc.ResolveProject(project);
        var projectObj = await TryGetProjectAsync(svc, projectPath, Tool, cancellationToken)
            ?? throw new McpException(
                $"{Tool}: project '{projectPath}' was not found, or the token cannot see it.");

        // shared_with_groups is the only place the access level appears, so it is the source of
        // truth here. invited_groups (below) knows about direct vs inherited but not the level.
        var shares = (projectObj["shared_with_groups"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(s => new
            {
                GroupId = s["group_id"]?.GetValue<long>(),
                Name = s["group_name"]?.GetValue<string>(),
                FullPath = s["group_full_path"]?.GetValue<string>(),
                AccessLevel = s["group_access_level"]?.GetValue<int>() ?? 0,
                ExpiresAt = s["expires_at"]?.GetValue<string?>(),
            })
            .ToList();

        // Enrichment, not a dependency: `relation` is the only way to tell a direct share from one
        // inherited via an ancestor group, but the endpoint is comparatively recent. On an older
        // self-managed GitLab this call fails and the relation is reported as unknown rather than
        // failing the whole verification, which is the part that actually matters.
        HashSet<long>? directGroupIds = null;
        string? relationNote = null;
        try
        {
            using var response = await svc.Http.GetAsync(
                $"projects/{Uri.EscapeDataString(projectPath)}/invited_groups?relation[]=direct&per_page=100",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                directGroupIds = (JsonNode.Parse(body) as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(g => g["id"]?.GetValue<long>())
                    .Where(id => id is not null)
                    .Select(id => id!.Value)
                    .ToHashSet();
            }
            else
            {
                relationNote = $"Could not determine direct vs inherited: GET invited_groups returned " +
                               $"HTTP {(int)response.StatusCode}. This endpoint is not present on older GitLab versions.";
            }
        }
        catch (HttpRequestException ex)
        {
            relationNote = $"Could not determine direct vs inherited: {ex.Message}";
        }

        var described = shares.Select(s => new
        {
            GroupId = s.GroupId,
            Name = s.Name,
            FullPath = s.FullPath,
            AccessLevel = s.AccessLevel,
            AccessLevelName = DescribeAccessLevel(s.AccessLevel),
            ExpiresAt = s.ExpiresAt,
            Relation = directGroupIds is null
                ? "unknown"
                : (s.GroupId is not null && directGroupIds.Contains(s.GroupId.Value) ? "direct" : "inherited"),
        }).ToList();

        // The filter is applied after collecting everything so the unfiltered total is still
        // reported — "0 of 3 shares matched" is a materially different answer from "0 shares".
        var wanted = group?.Trim().Trim('/');
        var matched = string.IsNullOrWhiteSpace(wanted)
            ? described
            : described.Where(s =>
                string.Equals(s.FullPath, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.GroupId?.ToString(), wanted, StringComparison.Ordinal)).ToList();

        return JsonSerializer.Serialize(new
        {
            Project = new
            {
                Path = projectObj["path_with_namespace"]?.GetValue<string>() ?? projectPath,
                Id = projectObj["id"]?.GetValue<long>(),
                Visibility = projectObj["visibility"]?.GetValue<string>(),
            },
            GroupFilter = string.IsNullOrWhiteSpace(wanted) ? null : wanted,
            Found = matched.Count > 0,
            MatchedCount = matched.Count,
            TotalShareCount = described.Count,
            Shares = matched,
            Note = relationNote,
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- helpers

    private static string NormalizeVisibility(string visibility, string tool) =>
        (visibility ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "private" => "private",
            "internal" => "internal",
            "public" => "public",
            _ => throw new McpException(
                $"{tool}: '{visibility}' is not a valid visibility. Use private, internal, or public."),
        };

    /// <summary>
    /// Resolve a namespace path to its numeric id. Matches the full path exactly (case-insensitively)
    /// and refuses anything ambiguous, so a project can never land in a namespace the caller did
    /// not name.
    /// </summary>
    private static async Task<(long Id, string FullPath)> ResolveNamespaceAsync(
        GitlabService svc, string requested, string tool, CancellationToken cancellationToken)
    {
        using var response = await svc.Http.GetAsync(
            $"namespaces?search={Uri.EscapeDataString(requested)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new McpException(DescribeFailure(tool, response.StatusCode, body,
                $"resolving namespace '{requested}'", null));
        }

        var matches = (JsonNode.Parse(body) as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(n => string.Equals(n["full_path"]?.GetValue<string>(), requested, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            throw new McpException(
                $"{tool}: namespace '{requested}' was not found, or the token cannot see it. " +
                "Pass the full path, e.g. 'parent-group/sub-group'.");
        }
        if (matches.Count > 1)
        {
            throw new McpException(
                $"{tool}: namespace '{requested}' matched {matches.Count} namespaces. Refusing to guess which one you meant.");
        }

        var match = matches[0];
        var kind = match["kind"]?.GetValue<string>();
        var fullPath = match["full_path"]?.GetValue<string>() ?? requested;

        // Personal namespaces are the user's own; only group namespaces face the allow-list.
        if (!string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase))
        {
            svc.EnsureGroupAllowed(fullPath);
        }

        return (match["id"]?.GetValue<long>() ?? throw new McpException(
            $"{tool}: namespace '{requested}' has no id in GitLab's response."), fullPath);
    }

    private static async Task<(long Id, string FullPath, string? Name)> ResolveGroupAsync(
        GitlabService svc, string requested, string tool, CancellationToken cancellationToken)
    {
        using var response = await svc.Http.GetAsync(
            $"groups/{Uri.EscapeDataString(requested)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new McpException(DescribeFailure(tool, response.StatusCode, body,
                $"resolving group '{requested}'",
                response.StatusCode == HttpStatusCode.NotFound
                    ? "Pass the group's full path, e.g. 'parent-group/sub-group', or its numeric id."
                    : null));
        }

        var obj = JsonNode.Parse(body) as JsonObject
            ?? throw new McpException($"{tool}: GitLab returned an unexpected shape for group '{requested}'.");

        var fullPath = obj["full_path"]?.GetValue<string>() ?? requested;

        // Guard against a numeric id or a partial path resolving to something else.
        if (!long.TryParse(requested, out _) &&
            !string.Equals(fullPath, requested, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"{tool}: group '{requested}' resolved to '{fullPath}', which is not an exact match.");
        }

        return (obj["id"]?.GetValue<long>() ?? throw new McpException(
            $"{tool}: group '{requested}' has no id in GitLab's response."), fullPath, obj["name"]?.GetValue<string>());
    }

    private static async Task<JsonObject?> TryGetProjectAsync(
        GitlabService svc, string pathWithNamespace, string tool, CancellationToken cancellationToken)
    {
        using var response = await svc.Http.GetAsync(
            $"projects/{Uri.EscapeDataString(pathWithNamespace)}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpException(DescribeFailure(tool, response.StatusCode, body,
                $"looking up project '{pathWithNamespace}'", null));
        }

        return JsonNode.Parse(body) as JsonObject;
    }

    private static (int AccessLevel, string? ExpiresAt)? FindExistingShare(JsonObject project, long groupId)
    {
        if (project["shared_with_groups"] is not JsonArray shares) return null;

        foreach (var share in shares.OfType<JsonObject>())
        {
            if (share["group_id"]?.GetValue<long>() != groupId) continue;
            return (share["group_access_level"]?.GetValue<int>() ?? 0,
                    share["expires_at"]?.GetValue<string?>());
        }
        return null;
    }

    /// <summary>
    /// Project fields worth returning. Deliberately an allow-list: GitLab's project payload can
    /// carry <c>http_url_to_repo</c> variants with embedded credentials on some configurations,
    /// and echoing the whole object would ship whatever future GitLab versions add.
    /// </summary>
    private static object SummariseProject(JsonObject p) => new
    {
        Id = p["id"]?.GetValue<long>(),
        Name = p["name"]?.GetValue<string>(),
        Path = p["path"]?.GetValue<string>(),
        PathWithNamespace = p["path_with_namespace"]?.GetValue<string>(),
        Visibility = p["visibility"]?.GetValue<string>(),
        DefaultBranch = p["default_branch"]?.GetValue<string?>(),
        HttpUrlToRepo = StripCredentials(p["http_url_to_repo"]?.GetValue<string>()),
        SshUrlToRepo = p["ssh_url_to_repo"]?.GetValue<string>(),
        WebUrl = p["web_url"]?.GetValue<string>(),
    };

    /// <summary>Drop any <c>user:token@</c> userinfo GitLab may have embedded in a clone URL.</summary>
    private static string? StripCredentials(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (string.IsNullOrEmpty(uri.UserInfo)) return url;

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Build an error that says what failed and what to do, including GitLab's own message but
    /// never the request headers or token.
    /// </summary>
    private static string DescribeFailure(
        string tool, HttpStatusCode status, string body, string what, string? hint)
    {
        var detail = ExtractMessage(body);
        var text = $"{tool} failed {what}: HTTP {(int)status} {status}.";
        if (hint is not null) text += " " + hint;
        if (!string.IsNullOrWhiteSpace(detail)) text += $" GitLab said: {detail}";
        return text;
    }

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
            {
                var message = obj["message"] ?? obj["error"];
                if (message is not null) return message.ToJsonString();
            }
        }
        catch (JsonException) { /* not JSON — fall through */ }

        return body.Length <= 400 ? body.Trim() : body[..400].Trim() + "…";
    }
}
