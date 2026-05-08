using System.ComponentModel;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class GroupTools
{
    [McpServerTool(Name = "get_group"),
     Description("Get details for a single GitLab group by full path (e.g. 'parent/child').")]
    public static async Task<string> GetGroup(
        GitlabService svc,
        [Description("Group full path. Falls back to Gitlab:DefaultNamespace.")] string? group = null)
    {
        if (!svc.Options.EnableGroups) throw new InvalidOperationException("Group tools are disabled.");
        var fullPath = svc.ResolveGroup(group);
        var g = await svc.Client.Groups.GetByFullPathAsync(fullPath);
        return JsonSerializer.Serialize(SummariseGroup(g), JsonOpts.Default);
    }

    [McpServerTool(Name = "list_group_projects"),
     Description("List projects belonging to a group.")]
    public static async Task<string> ListGroupProjects(
        GitlabService svc,
        [Description("Group full path. Falls back to Gitlab:DefaultNamespace.")] string? group = null,
        [Description("If true, include projects from subgroups too.")] bool includeSubgroups = false)
    {
        if (!svc.Options.EnableGroups) throw new InvalidOperationException("Group tools are disabled.");
        var fullPath = svc.ResolveGroup(group);
        var g = await svc.Client.Groups.GetByFullPathAsync(fullPath);

        var query = new GroupProjectsQuery
        {
            IncludeSubGroups = includeSubgroups,
        };

        var results = new List<object>();
        await foreach (var p in svc.Client.Groups.GetProjectsAsync(g.Id, query))
        {
            results.Add(ProjectTools.SummariseProject(p));
            if (results.Count >= svc.Options.DefaultPageSize * svc.Options.MaxPages) break;
        }
        return JsonSerializer.Serialize(results, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_subgroups"),
     Description("List subgroups of a group.")]
    public static async Task<string> ListSubgroups(
        GitlabService svc,
        [Description("Parent group full path. Falls back to Gitlab:DefaultNamespace.")] string? group = null)
    {
        if (!svc.Options.EnableGroups) throw new InvalidOperationException("Group tools are disabled.");
        var fullPath = svc.ResolveGroup(group);

        var results = new List<object>();
        await foreach (var sub in svc.Client.Groups.GetSubgroupsByFullPathAsync(fullPath))
        {
            results.Add(SummariseGroup(sub));
            if (results.Count >= svc.Options.DefaultPageSize * svc.Options.MaxPages) break;
        }
        return JsonSerializer.Serialize(results, JsonOpts.Default);
    }

    [McpServerTool(Name = "search_groups"),
     Description("Search for groups by name across the GitLab instance.")]
    public static async Task<string> SearchGroups(
        GitlabService svc,
        [Description("Search term.")] string search)
    {
        if (!svc.Options.EnableGroups) throw new InvalidOperationException("Group tools are disabled.");
        var results = new List<object>();
        await foreach (var g in svc.Client.Groups.SearchAsync(search))
        {
            results.Add(SummariseGroup(g));
            if (results.Count >= svc.Options.DefaultPageSize * svc.Options.MaxPages) break;
        }
        return JsonSerializer.Serialize(results, JsonOpts.Default);
    }

    private static object SummariseGroup(Group g) => new
    {
        g.Id,
        g.Name,
        g.Path,
        g.FullName,
        g.FullPath,
        g.Description,
        Visibility = g.Visibility.ToString(),
        g.ParentId,
        g.CreatedAt,
    };
}
