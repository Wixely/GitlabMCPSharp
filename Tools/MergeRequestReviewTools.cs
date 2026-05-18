using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class MergeRequestReviewTools
{
    private static void EnsureMr(GitlabService svc)
    {
        if (!svc.Options.EnableMergeRequests)
            throw new InvalidOperationException("Merge request tools are disabled.");
    }

    private static async Task<(NGitLab.IMergeRequestClient client, long projectId)> ResolveAsync(GitlabService svc, string? project)
    {
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        return (svc.Client.GetMergeRequest(p.Id), p.Id);
    }

    [McpServerTool(Name = "list_merge_request_changes"),
     Description("List files changed in a merge request with per-file unified diff. Use this to read the code under review.")]
    public static async Task<string> ListChanges(
        GitlabService svc,
        [Description("Merge request IID (per-project number).")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null,
        [Description("If true, omit per-file diff content (metadata only).")] bool omitDiff = false)
    {
        EnsureMr(svc);
        var (client, _) = await ResolveAsync(svc, project);
        var mrc = client.Changes(iid).MergeRequestChange;
        var summary = mrc.Changes?.Select(c => new
        {
            c.OldPath,
            c.NewPath,
            c.NewFile,
            c.RenamedFile,
            c.DeletedFile,
            diff = omitDiff ? null : c.Diff,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_merge_request_diff"),
     Description("Return the concatenated unified diff for a merge request (joining each file's diff).")]
    public static async Task<string> GetDiff(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        var (client, _) = await ResolveAsync(svc, project);
        var mrc = client.Changes(iid).MergeRequestChange;
        var sb = new StringBuilder();
        foreach (var c in mrc.Changes ?? Array.Empty<Change>())
        {
            sb.AppendLine($"diff --git a/{c.OldPath} b/{c.NewPath}");
            sb.AppendLine(c.Diff);
        }
        return JsonSerializer.Serialize(new { iid, diff = sb.ToString() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_merge_request_approval_state"),
     Description("Return the approval state of a merge request: required approvals, who has approved, and whether the caller can approve.")]
    public static async Task<string> GetApprovalState(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        var (client, _) = await ResolveAsync(svc, project);
        var ac = client.ApprovalClient(iid);
        var approvals = ac.Approvals;
        var summary = new
        {
            iid,
            approvals.Approved,
            approvals.ApprovalsRequired,
            approvals.ApprovalsLeft,
            approvals.UserHasApproved,
            approvals.UserCanApprove,
            ApprovedBy = approvals.ApprovedBy?.Select(a => a.User?.Username).ToArray() ?? Array.Empty<string>(),
            Approvers = approvals.Approvers?.Select(a => a.User?.Username).ToArray() ?? Array.Empty<string>(),
            SuggestedApprovers = approvals.SuggestedApprovers?.Select(u => u.Username).ToArray() ?? Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_merge_request_discussions"),
     Description("List discussion threads on a merge request (each thread contains one or more notes).")]
    public static async Task<string> ListDiscussions(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        var (client, _) = await ResolveAsync(svc, project);
        var dc = client.Discussions(iid);
        var discussions = dc.All
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(d => new
            {
                d.Id,
                d.IndividualNote,
                notes = d.Notes?.Select(n => new
                {
                    n.Id,
                    n.Body,
                    author = n.Author?.Username,
                    n.CreatedAt,
                    n.UpdatedAt,
                    n.Resolved,
                    n.Resolvable,
                    n.System,
                    n.Type,
                }),
            });
        return JsonSerializer.Serialize(discussions, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_merge_request_notes"),
     Description("List notes (flat) on a merge request — the conversation/issue-style comments.")]
    public static async Task<string> ListNotes(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        var (client, _) = await ResolveAsync(svc, project);
        var cc = client.Comments(iid);
        var notes = cc.All
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(n => new
            {
                n.Id,
                n.Body,
                author = n.Author?.Username,
                n.CreatedAt,
                n.UpdatedAt,
                n.Resolved,
                n.Resolvable,
                n.System,
                n.Type,
            });
        return JsonSerializer.Serialize(notes, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_merge_request_pipelines"),
     Description("List CI pipelines attached to a merge request (latest head pipeline status etc.).")]
    public static async Task<string> ListPipelines(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        var (client, _) = await ResolveAsync(svc, project);
        var pipelines = client.GetPipelines(iid)
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(p => new
            {
                p.Id,
                p.Status,
                p.Ref,
                p.Sha,
                p.WebUrl,
                p.CreatedAt,
                p.UpdatedAt,
            });
        return JsonSerializer.Serialize(pipelines, JsonOpts.Default);
    }

    [McpServerTool(Name = "approve_merge_request"),
     Description("Approve a merge request as the authenticated user. Requires write mode.")]
    public static async Task<string> Approve(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Optional SHA the approval is pinned to (must match the current head if set).")] string? sha = null,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("approve_merge_request");
        var (client, _) = await ResolveAsync(svc, project);
        var result = client.ApprovalClient(iid).ApproveMergeRequest(new MergeRequestApproveRequest { Sha = sha });
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new { iid, approved = result.Approved, approvalsLeft = result.ApprovalsLeft }, JsonOpts.Default);
    }

    [McpServerTool(Name = "unapprove_merge_request"),
     Description("Remove the authenticated user's approval from a merge request. Requires write mode.")]
    public static async Task<string> Unapprove(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null,
        CancellationToken ct = default)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("unapprove_merge_request");
        var (_, projectId) = await ResolveAsync(svc, project);
        using var response = await svc.Http.PostAsync($"projects/{projectId}/merge_requests/{iid}/unapprove", content: null, ct);
        if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NoContent)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"GitLab unapprove returned {(int)response.StatusCode}: {body}");
        }
        return JsonSerializer.Serialize(new { iid, unapproved = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "add_merge_request_note"),
     Description("Add a conversation/issue-style note (comment) to a merge request. Requires write mode.")]
    public static async Task<string> AddNote(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Note body markdown.")] string body,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("add_merge_request_note");
        var (client, _) = await ResolveAsync(svc, project);
        var created = client.Comments(iid).Add(new MergeRequestCommentCreate { Body = body });
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new { created.Id, created.Body }, JsonOpts.Default);
    }

    [McpServerTool(Name = "add_merge_request_discussion"),
     Description("Start a new discussion thread on a merge request. Requires write mode.")]
    public static async Task<string> AddDiscussion(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Discussion body markdown.")] string body,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("add_merge_request_discussion");
        var (client, _) = await ResolveAsync(svc, project);
        var created = client.Discussions(iid).Add(new MergeRequestDiscussionCreate { Body = body });
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new { created.Id, notes = created.Notes?.Length ?? 0 }, JsonOpts.Default);
    }

    [McpServerTool(Name = "close_merge_request"),
     Description("Close a merge request without merging. Requires write mode.")]
    public static async Task<string> Close(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("close_merge_request");
        var (client, _) = await ResolveAsync(svc, project);
        var mr = client.Close(iid);
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new { mr.Iid, mr.State, mr.WebUrl }, JsonOpts.Default);
    }

    [McpServerTool(Name = "reopen_merge_request"),
     Description("Reopen a previously closed merge request. Requires write mode.")]
    public static async Task<string> Reopen(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("reopen_merge_request");
        var (client, _) = await ResolveAsync(svc, project);
        var mr = client.Reopen(iid);
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new { mr.Iid, mr.State, mr.WebUrl }, JsonOpts.Default);
    }
}
