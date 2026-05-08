using System.ComponentModel;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class ReleaseTools
{
    [McpServerTool(Name = "list_releases"),
     Description("List releases for a project.")]
    public static async Task<string> ListReleases(
        GitlabService svc,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);

        var releases = new List<object>();
        await foreach (var r in svc.Client.GetReleases(p.Id).GetAsync())
        {
            releases.Add(SummariseRelease(r));
            if (releases.Count >= svc.Options.DefaultPageSize * svc.Options.MaxPages) break;
        }
        return JsonSerializer.Serialize(releases, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_release"),
     Description("Get a single release by tag name.")]
    public static async Task<string> GetRelease(
        GitlabService svc,
        [Description("Tag name of the release.")] string tagName,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var release = svc.Client.GetReleases(p.Id)[tagName];
        return JsonSerializer.Serialize(SummariseRelease(release, includeBody: true), JsonOpts.Default);
    }

    private static object SummariseRelease(ReleaseInfo r, bool includeBody = false) => new
    {
        r.TagName,
        r.Name,
        r.CreatedAt,
        r.ReleasedAt,
        Author = r.Author?.Username,
        CommitSha = r.Commit?.Id.ToString(),
        r.TagPath,
        r.CommitPath,
        Description = includeBody ? r.Description : null,
        Sources = r.Assets?.Sources?.Select(s => new { s.Format, s.Url }),
        AssetCount = r.Assets?.Count,
    };
}
