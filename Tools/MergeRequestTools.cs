using System.ComponentModel;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class MergeRequestTools
{
    [McpServerTool(Name = "list_merge_requests"),
     Description("List merge requests in a project.")]
    public static async Task<string> ListMergeRequests(
        GitlabService svc,
        [Description("Status filter: opened, closed, merged, locked, all (default opened).")] string state = "opened",
        [Description("Optional source branch filter.")] string? sourceBranch = null,
        [Description("Optional target branch filter.")] string? targetBranch = null,
        [Description("Optional comma-separated label list.")] string? labels = null,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableMergeRequests) throw new InvalidOperationException("Merge request tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var mrClient = svc.Client.GetMergeRequest(p.Id);

        var query = new MergeRequestQuery
        {
            State = state.ToLowerInvariant() switch
            {
                "closed" => MergeRequestState.closed,
                "merged" => MergeRequestState.merged,
                "locked" => MergeRequestState.locked,
                "all" => null,
                _ => MergeRequestState.opened,
            },
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch,
            Labels = labels,
            PerPage = svc.Options.DefaultPageSize,
        };

        var mrs = mrClient.Get(query)
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(mr => SummariseMergeRequest(mr));
        return JsonSerializer.Serialize(mrs, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_merge_request"),
     Description("Get a single merge request by IID.")]
    public static async Task<string> GetMergeRequest(
        GitlabService svc,
        [Description("Merge request IID (per-project number).")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableMergeRequests) throw new InvalidOperationException("Merge request tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var mrClient = svc.Client.GetMergeRequest(p.Id);
        var mr = await mrClient.GetByIidAsync(iid, new SingleMergeRequestQuery());
        return JsonSerializer.Serialize(SummariseMergeRequest(mr, includeBody: true), JsonOpts.Default);
    }

    private static object SummariseMergeRequest(MergeRequest mr, bool includeBody = false) => new
    {
        mr.Iid,
        mr.Id,
        mr.Title,
        mr.State,
        Author = mr.Author?.Username,
        Assignees = mr.Assignees?.Select(a => a.Username).ToArray() ?? Array.Empty<string>(),
        Reviewers = mr.Reviewers?.Select(r => r.Username).ToArray() ?? Array.Empty<string>(),
        mr.SourceBranch,
        mr.TargetBranch,
        mr.Draft,
        mr.MergeStatus,
        mr.HasConflicts,
        mr.Sha,
        mr.MergeCommitSha,
        mr.CreatedAt,
        mr.UpdatedAt,
        mr.MergedAt,
        mr.ClosedAt,
        mr.WebUrl,
        Labels = mr.Labels ?? Array.Empty<string>(),
        Description = includeBody ? mr.Description : null,
    };
}
