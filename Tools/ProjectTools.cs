using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitlabMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "gl_get_project"),
     Description("Get details for a single GitLab project by namespaced path (e.g. 'group/project').")]
    public static async Task<string> GetProject(
        GitlabService svc,
        [Description("Project namespaced path, e.g. 'my-group/my-project'. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        return JsonSerializer.Serialize(SummariseProject(p), JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_update_project_description"),
     Description("Update a GitLab project's short description. Pass an empty string to clear it. Requires write mode and Maintainer or Owner access.")]
    public static async Task<string> UpdateProjectDescription(
        GitlabService svc,
        [Description("New short project description. Pass an empty string to clear it.")] string description,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(description);
        svc.EnsureWriteAllowed("update_project_description");
        var path = svc.ResolveProject(project);
        var normalizedDescription = TextUtil.NormalizeNewlines(description);
        var payload = new Dictionary<string, object?>
        {
            ["description"] = normalizedDescription,
        };
        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var response = await svc.Http.PutAsync(
            $"projects/{Uri.EscapeDataString(path)}",
            content,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"gl_update_project_description failed for project '{path}': " +
                $"HTTP {(int)response.StatusCode} {response.StatusCode}. Response body: {responseBody}");
        }

        return JsonSerializer.Serialize(new
        {
            Project = path,
            Description = normalizedDescription,
            Cleared = normalizedDescription.Length == 0,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_update_project_avatar"),
     Description("Upload or remove a GitLab project's avatar. Pass the image inline as base64 or a data URI (e.g. 'data:image/png;base64,...'); server-side file paths are not accepted. " +
                 "Supported formats: png, jpg/jpeg, gif, bmp, tiff, ico, webp; maximum 200 KB (192 x 192 pixels recommended). " +
                 "Set remove=true (and omit image) to delete the avatar. Returns the resulting avatar_url, read back from GitLab. " +
                 "Requires write mode, an 'api'-scoped token and Maintainer or Owner access.")]
    public static async Task<string> UpdateProjectAvatar(
        GitlabService svc,
        [Description("Image content as base64, or a data URI such as 'data:image/png;base64,...'. Omit when remove=true.")] string? image = null,
        [Description("Filename for the upload, e.g. 'logo.png'. Its extension must match the image. Defaults to 'avatar.<detected extension>'.")] string? fileName = null,
        [Description("Optional media type, e.g. 'image/png'. Checked against the image content.")] string? mediaType = null,
        [Description("Set true to remove the current avatar instead of uploading one.")] bool remove = false,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        const string Tool = "gl_update_project_avatar";

        svc.EnsureWriteAllowed("update_project_avatar");
        var path = svc.ResolveProject(project);

        if (remove && !string.IsNullOrWhiteSpace(image))
            throw new McpException($"{Tool}: pass either image or remove=true, not both.");
        if (!remove && string.IsNullOrWhiteSpace(image))
            throw new McpException($"{Tool}: no image supplied. Pass the image as base64 or a data URI, or set remove=true to delete the avatar.");

        ProjectAvatar.Image? avatar = null;
        HttpContent content;
        if (remove)
        {
            // GitLab removes the avatar when the attribute is sent blank.
            content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("avatar", string.Empty) });
        }
        else
        {
            avatar = ProjectAvatar.Parse(image!, fileName, mediaType, Tool);
            var file = new ByteArrayContent(avatar.Bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(avatar.Format.MediaType);
            var multipart = new MultipartFormDataContent();
            multipart.Add(file, "avatar", avatar.FileName);
            content = multipart;
        }

        var endpoint = $"projects/{Uri.EscapeDataString(path)}";
        string body;
        using (content)
        using (var response = await svc.Http.PutAsync(endpoint, content, cancellationToken))
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new McpException(ProjectProvisioningTools.DescribeFailure(Tool, response.StatusCode, body,
                    remove ? $"removing the avatar of project '{path}'" : $"uploading an avatar to project '{path}'",
                    response.StatusCode switch
                    {
                        HttpStatusCode.BadRequest => "GitLab rejected the avatar — usually an unsupported file type or a file over the 200 KB limit.",
                        HttpStatusCode.Unauthorized => "The token is missing, expired or invalid.",
                        HttpStatusCode.Forbidden => "The token needs the 'api' scope and Maintainer or Owner access to the project.",
                        HttpStatusCode.NotFound => "The project was not found, or the token cannot see it.",
                        HttpStatusCode.RequestEntityTooLarge => "The request was too large for GitLab or a proxy in front of it.",
                        _ => null,
                    }));
            }
        }

        var written = ReadAvatarUrl(body);

        // Read back rather than trusting the write response, so the caller sees the live state.
        string? avatarUrl = written;
        var verified = false;
        string? warning = null;
        try
        {
            using var readback = await svc.Http.GetAsync(endpoint, cancellationToken);
            var readbackBody = await readback.Content.ReadAsStringAsync(cancellationToken);
            if (readback.IsSuccessStatusCode)
            {
                avatarUrl = ReadAvatarUrl(readbackBody);
                verified = remove ? string.IsNullOrEmpty(avatarUrl) : !string.IsNullOrEmpty(avatarUrl);
                if (!verified)
                {
                    warning = remove
                        ? "GitLab accepted the removal, but the project still reports an avatar_url."
                        : "GitLab accepted the upload, but the project reports no avatar_url.";
                }
            }
            else
            {
                warning = $"The update succeeded, but reading the project back failed (HTTP {(int)readback.StatusCode}); avatar_url is from the update response.";
            }
        }
        catch (HttpRequestException ex)
        {
            warning = $"The update succeeded, but reading the project back failed ({ex.Message}); avatar_url is from the update response.";
        }

        return JsonSerializer.Serialize(new
        {
            Project = path,
            Action = remove ? "Removed" : "Uploaded",
            AvatarUrl = avatarUrl,
            Verified = verified,
            FileName = avatar?.FileName,
            MediaType = avatar?.Format.MediaType,
            SizeBytes = avatar?.Bytes.Length,
            Warning = warning,
        }, JsonOpts.Default);
    }

    private static string? ReadAvatarUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return (JsonNode.Parse(body) as JsonObject)?["avatar_url"]?.GetValue<string?>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    [McpServerTool(Name = "gl_list_my_projects"),
     Description("List projects accessible to the authenticated user.")]
    public static Task<string> ListMyProjects(
        GitlabService svc,
        [Description("Filter: accessible, owned, visible. Defaults to accessible.")] string? scope = null,
        [Description("Free text search applied client-side to project name and path.")] string? search = null)
    {
        var which = scope?.ToLowerInvariant() switch
        {
            "owned" => svc.Client.Projects.Owned,
            "visible" => svc.Client.Projects.Visible,
            _ => svc.Client.Projects.Accessible,
        };

        var page = which.Take(svc.Options.DefaultPageSize * svc.Options.MaxPages);
        if (!string.IsNullOrWhiteSpace(search))
        {
            page = page.Where(p =>
                (p.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.PathWithNamespace?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var summary = page.Select(SummariseProject);
        return Task.FromResult(JsonSerializer.Serialize(summary, JsonOpts.Default));
    }

    [McpServerTool(Name = "gl_list_branches"),
     Description("List branches in a project.")]
    public static Task<string> ListBranches(
        GitlabService svc,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableRepository) throw new InvalidOperationException("Repository tools are disabled.");
        var path = svc.ResolveProject(project);
        var repo = svc.Client.GetRepository((ProjectId)path);
        var branches = repo.Branches.All
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(b => new
            {
                b.Name,
                b.Protected,
                b.Default,
                b.Merged,
                Sha = b.Commit?.Id.ToString(),
                AuthoredAt = b.Commit?.AuthoredDate,
                Message = b.Commit?.Message,
            });
        return Task.FromResult(JsonSerializer.Serialize(branches, JsonOpts.Default));
    }

    [McpServerTool(Name = "gl_list_tags"),
     Description("List tags in a project.")]
    public static Task<string> ListTags(
        GitlabService svc,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableRepository) throw new InvalidOperationException("Repository tools are disabled.");
        var path = svc.ResolveProject(project);
        var repo = svc.Client.GetRepository((ProjectId)path);
        var tags = repo.Tags.All
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(t => new
            {
                t.Name,
                t.Message,
                Sha = t.Commit?.Id.ToString(),
                Protected = t.Protected,
            });
        return Task.FromResult(JsonSerializer.Serialize(tags, JsonOpts.Default));
    }

    [McpServerTool(Name = "gl_list_repository_tree"),
     Description("List entries (files and folders) in a project's repository tree at a given path.")]
    public static async Task<string> ListRepositoryTree(
        GitlabService svc,
        [Description("Path within the repo. Empty for root.")] string? path = null,
        [Description("Branch, tag, or commit sha. Defaults to the project's default branch.")] string? @ref = null,
        [Description("If true, recurse into subdirectories.")] bool recursive = false,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableRepository) throw new InvalidOperationException("Repository tools are disabled.");
        var projectPath = svc.ResolveProject(project);
        var repo = svc.Client.GetRepository((ProjectId)projectPath);
        var options = new RepositoryGetTreeOptions
        {
            Path = path ?? string.Empty,
            Ref = @ref ?? string.Empty,
            Recursive = recursive,
        };
        var entries = new List<object>();
        await foreach (var t in repo.GetTreeAsync(options))
        {
            entries.Add(new { t.Id, t.Name, t.Path, Type = t.Type.ToString(), t.Mode });
            if (entries.Count >= svc.Options.DefaultPageSize * svc.Options.MaxPages) break;
        }
        return JsonSerializer.Serialize(entries, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_get_file_contents"),
     Description("Get file contents at a path on a given ref. Decodes base64 content for convenience.")]
    public static async Task<string> GetFileContents(
        GitlabService svc,
        [Description("File path inside the repository.")] string filePath,
        [Description("Branch, tag, or commit sha. Defaults to HEAD of the default branch.")] string? @ref = null,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableRepository) throw new InvalidOperationException("Repository tools are disabled.");
        var projectPath = svc.ResolveProject(project);
        var repo = svc.Client.GetRepository((ProjectId)projectPath);
        var data = await repo.Files.GetAsync(filePath, @ref ?? "HEAD");
        var decoded = data.DecodedContent;
        var summary = new
        {
            data.Name,
            data.Path,
            data.Size,
            data.Encoding,
            data.Ref,
            data.BlobId,
            data.CommitId,
            data.LastCommitId,
            ContentPreview = decoded is { Length: > 4096 } ? decoded[..4096] + "…(truncated)" : decoded,
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_list_commits"),
     Description("List recent commits for a branch or ref.")]
    public static Task<string> ListCommits(
        GitlabService svc,
        [Description("Branch, tag, or sha. Defaults to the default branch.")] string? @ref = null,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null,
        [Description("Maximum commits to return (capped by DefaultPageSize x MaxPages).")] int max = 50)
    {
        if (!svc.Options.EnableRepository) throw new InvalidOperationException("Repository tools are disabled.");
        var projectPath = svc.ResolveProject(project);
        var repo = svc.Client.GetRepository((ProjectId)projectPath);
        var cap = Math.Min(max, svc.Options.DefaultPageSize * svc.Options.MaxPages);
        var commits = repo.GetCommits(@ref ?? string.Empty, cap)
            .Select(c => new
            {
                Id = c.Id.ToString(),
                ShortId = c.ShortId,
                c.Title,
                c.Message,
                c.AuthorName,
                c.AuthorEmail,
                c.AuthoredDate,
                c.CommittedDate,
                c.WebUrl,
            });
        return Task.FromResult(JsonSerializer.Serialize(commits, JsonOpts.Default));
    }

    internal static object SummariseProject(Project p) => new
    {
        p.Id,
        p.Name,
        p.NameWithNamespace,
        p.PathWithNamespace,
        p.Description,
        p.DefaultBranch,
        Visibility = p.VisibilityLevel.ToString(),
        p.Archived,
        p.WebUrl,
        p.SshUrl,
        p.HttpUrl,
        p.StarCount,
        p.ForksCount,
        p.OpenIssuesCount,
        p.LastActivityAt,
        Topics = p.Topics ?? Array.Empty<string>(),
    };
}
