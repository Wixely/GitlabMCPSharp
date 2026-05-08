using System.ComponentModel;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "get_project"),
     Description("Get details for a single GitLab project by namespaced path (e.g. 'group/project').")]
    public static async Task<string> GetProject(
        GitlabService svc,
        [Description("Project namespaced path, e.g. 'my-group/my-project'. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        return JsonSerializer.Serialize(SummariseProject(p), JsonOpts.Default);
    }

    [McpServerTool(Name = "list_my_projects"),
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

    [McpServerTool(Name = "list_branches"),
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

    [McpServerTool(Name = "list_tags"),
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

    [McpServerTool(Name = "list_repository_tree"),
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

    [McpServerTool(Name = "get_file_contents"),
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

    [McpServerTool(Name = "list_commits"),
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
