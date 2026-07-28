using System.ComponentModel;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class MergeRequestTools
{
    [McpServerTool(Name = "gl_list_merge_requests"),
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

    [McpServerTool(Name = "gl_get_merge_request"),
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

    [McpServerTool(Name = "gl_create_merge_request"),
     Description("Create a merge request from a source branch into a target branch. Requires write mode.")]
    public static async Task<string> CreateMergeRequest(
        GitlabService svc,
        [Description("MR title.")] string title,
        [Description("Source branch with the changes.")] string sourceBranch,
        [Description("Target branch to merge into (e.g. main).")] string targetBranch,
        [Description("Optional description / body markdown.")] string? description = null,
        [Description("Open as a draft MR (prefixes the title with 'Draft:'). Default false.")] bool draft = false,
        [Description("Remove the source branch when the MR is merged. Default false.")] bool removeSourceBranch = false,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnableMergeRequests) throw new InvalidOperationException("Merge request tools are disabled.");
        svc.EnsureWriteAllowed("create_merge_request");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required.", nameof(title));
        if (string.IsNullOrWhiteSpace(sourceBranch)) throw new ArgumentException("sourceBranch is required.", nameof(sourceBranch));
        if (string.IsNullOrWhiteSpace(targetBranch)) throw new ArgumentException("targetBranch is required.", nameof(targetBranch));

        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var mrClient = svc.Client.GetMergeRequest(p.Id);

        var create = new MergeRequestCreate
        {
            Title = draft && !title.StartsWith("Draft:", StringComparison.OrdinalIgnoreCase) ? $"Draft: {title}" : title,
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch,
            Description = TextUtil.NormalizeNewlines(description),
            RemoveSourceBranch = removeSourceBranch,
        };
        var mr = mrClient.Create(create);
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
