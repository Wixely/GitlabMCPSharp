using System.ComponentModel;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class IssueTools
{
    [McpServerTool(Name = "list_issues"),
     Description("List issues in a project. Supports incremental polling via updatedSinceUtc.")]
    public static async Task<string> ListIssues(
        GitlabService svc,
        [Description("Status filter: opened, closed, all (default opened).")] string state = "opened",
        [Description("Optional comma-separated label list to filter by.")] string? labels = null,
        [Description("Optional free-text search applied to title and description.")] string? search = null,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null,
        [Description("ISO-8601 UTC timestamp. Only return issues updated after this. Omit for no lower bound.")] string? updatedSinceUtc = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);

        var query = new IssueQuery
        {
            State = state.ToLowerInvariant() switch
            {
                "closed" => IssueState.closed,
                "all" => null,
                _ => IssueState.opened,
            },
            PerPage = svc.Options.DefaultPageSize,
            Labels = labels,
            Search = search,
        };
        if (!string.IsNullOrWhiteSpace(updatedSinceUtc))
        {
            query.UpdatedAfter = DateTime.Parse(updatedSinceUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        }

        var issues = svc.Client.Issues.Get(p.Id, query)
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(i => SummariseIssue(i));
        return JsonSerializer.Serialize(issues, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_issue"),
     Description("Get a single issue including body.")]
    public static async Task<string> GetIssue(
        GitlabService svc,
        [Description("Issue IID (per-project number, not global Id).")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var issue = await svc.Client.Issues.GetAsync(p.Id, iid);
        return JsonSerializer.Serialize(SummariseIssue(issue, includeBody: true), JsonOpts.Default);
    }

    [McpServerTool(Name = "create_issue"),
     Description("Create a new issue. Requires write mode.")]
    public static async Task<string> CreateIssue(
        GitlabService svc,
        [Description("Issue title.")] string title,
        [Description("Issue description markdown (optional).")] string? description = null,
        [Description("Optional comma-separated label list.")] string? labels = null,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        svc.EnsureWriteAllowed("create_issue");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);

        var create = new IssueCreate
        {
            ProjectId = p.Id,
            Title = title,
            Description = description,
            Labels = labels,
        };
        var created = await svc.Client.Issues.CreateAsync(create);
        return JsonSerializer.Serialize(new { created.IssueId, created.Id, created.WebUrl }, JsonOpts.Default);
    }

    private static object SummariseIssue(Issue i, bool includeBody = false) => new
    {
        Iid = i.IssueId,
        i.Id,
        i.Title,
        i.State,
        Author = i.Author?.Username,
        i.CreatedAt,
        i.UpdatedAt,
        i.ClosedAt,
        i.WebUrl,
        i.Confidential,
        Labels = i.Labels ?? Array.Empty<string>(),
        Assignees = i.Assignees?.Select(a => a.Username).ToArray() ?? Array.Empty<string>(),
        Description = includeBody ? i.Description : null,
    };
}
