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

    [McpServerTool(Name = "gl_list_merge_request_changes"),
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

    [McpServerTool(Name = "gl_get_merge_request_diff"),
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

    [McpServerTool(Name = "gl_get_merge_request_approval_state"),
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

    [McpServerTool(Name = "gl_list_merge_request_discussions"),
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

    [McpServerTool(Name = "gl_list_merge_request_notes"),
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

    [McpServerTool(Name = "gl_list_merge_request_pipelines"),
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

    [McpServerTool(Name = "gl_approve_merge_request"),
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

    [McpServerTool(Name = "gl_unapprove_merge_request"),
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

    [McpServerTool(Name = "gl_add_merge_request_note"),
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
        var created = client.Comments(iid).Add(new MergeRequestCommentCreate { Body = TextUtil.NormalizeNewlines(body) });
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new { created.Id, created.Body }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_add_merge_request_discussion"),
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
        var created = client.Discussions(iid).Add(new MergeRequestDiscussionCreate { Body = TextUtil.NormalizeNewlines(body) });
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new { created.Id, notes = created.Notes?.Length ?? 0 }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_add_merge_request_review_comment"),
     Description("Add an inline review comment anchored to a file and line in a merge request diff, as a new discussion thread. Set side to right (the new file, default) or left (the old file). The body supports markdown. Requires write mode.")]
    public static async Task<string> AddReviewComment(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("File path inside the repository (e.g. src/Foo.cs).")] string path,
        [Description("Comment body markdown.")] string body,
        [Description("Line number in the file to anchor the comment to.")] int line,
        [Description("Diff side: right (new file, default) or left (old file).")] string side = "right",
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null,
        CancellationToken ct = default)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("add_merge_request_review_comment");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required.", nameof(path));
        if (line <= 0) throw new ArgumentException("line must be a positive line number.", nameof(line));

        var normalizedSide = side.ToLowerInvariant();
        if (normalizedSide is not ("left" or "right"))
            throw new ArgumentException($"Unknown side '{side}'. Expected 'left' or 'right'.", nameof(side));

        var (client, projectId) = await ResolveAsync(svc, project);
        var mr = await client.GetByIidAsync(iid, new SingleMergeRequestQuery());
        var refs = mr.DiffRefs
            ?? throw new InvalidOperationException("Merge request has no diff refs yet (no commits to diff against).");

        var position = new Dictionary<string, object?>
        {
            ["base_sha"] = refs.BaseSha,
            ["head_sha"] = refs.HeadSha,
            ["start_sha"] = refs.StartSha,
            ["new_path"] = path,
            ["old_path"] = path,
            ["position_type"] = "text",
        };
        // right side comments on the new file (new_line); left side comments on the old file (old_line).
        if (normalizedSide == "left") position["old_line"] = line;
        else position["new_line"] = line;

        var payload = new Dictionary<string, object?> { ["body"] = TextUtil.NormalizeNewlines(body), ["position"] = position };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await svc.Http.PostAsync($"projects/{projectId}/merge_requests/{iid}/discussions", content, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"gl_add_merge_request_review_comment failed for MR !{iid}: HTTP {(int)resp.StatusCode} {resp.StatusCode}. " +
                "Common causes: the line/path is not part of the MR diff, or the file was not changed on that side. " +
                $"Response body: {respBody}");

        using var doc = JsonDocument.Parse(respBody);
        var discussionId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        int? noteId = null;
        if (doc.RootElement.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array && notes.GetArrayLength() > 0
            && notes[0].TryGetProperty("id", out var noteIdEl))
            noteId = noteIdEl.GetInt32();

        return JsonSerializer.Serialize(new { discussionId, noteId, path, side = normalizedSide, line }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_request_merge_request_reviewers"),
     Description("Request one or more reviewers on a merge request — i.e. formally request a code review. Each reviewer may be a numeric user id or a username, which is resolved via the users API. Replaces the current reviewer set. Requires write mode.")]
    public static async Task<string> RequestReviewers(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Reviewers: numeric user ids or usernames.")] string[] reviewers,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null,
        CancellationToken ct = default)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("request_merge_request_reviewers");
        if (reviewers is null || reviewers.Length == 0)
            throw new ArgumentException("Provide at least one reviewer.", nameof(reviewers));

        var (_, projectId) = await ResolveAsync(svc, project);

        var ids = new List<long>();
        foreach (var reviewer in reviewers)
            ids.Add(await ResolveUserIdAsync(svc, reviewer, ct));

        var payload = new Dictionary<string, object?> { ["reviewer_ids"] = ids };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await svc.Http.PutAsync($"projects/{projectId}/merge_requests/{iid}", content, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"gl_request_merge_request_reviewers failed for MR !{iid}: HTTP {(int)resp.StatusCode} {resp.StatusCode}.\n{respBody}");

        using var doc = JsonDocument.Parse(respBody);
        var assigned = doc.RootElement.TryGetProperty("reviewers", out var rv) && rv.ValueKind == JsonValueKind.Array
            ? rv.EnumerateArray().Select(u => u.TryGetProperty("username", out var un) ? un.GetString() : null).Where(u => u != null).ToArray()
            : Array.Empty<string?>();
        return JsonSerializer.Serialize(new { iid, reviewerIds = ids, reviewers = assigned }, JsonOpts.Default);
    }

    /// <summary>Resolve a numeric user id or username to a GitLab user id.</summary>
    private static async Task<long> ResolveUserIdAsync(GitlabService svc, string reviewer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
            throw new ArgumentException("Reviewer value cannot be empty.");
        if (long.TryParse(reviewer, out var numeric)) return numeric;

        var name = reviewer.StartsWith('@') ? reviewer[1..] : reviewer;
        using var resp = await svc.Http.GetAsync($"users?username={Uri.EscapeDataString(name)}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not resolve reviewer '{reviewer}': users lookup returned {(int)resp.StatusCode}.\n{body}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
            && doc.RootElement[0].TryGetProperty("id", out var idEl))
            return idEl.GetInt64();
        throw new InvalidOperationException($"No GitLab user matched username '{reviewer}'. Pass the numeric user id directly if needed.");
    }

    [McpServerTool(Name = "gl_close_merge_request"),
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

    [McpServerTool(Name = "gl_reopen_merge_request"),
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

    [McpServerTool(Name = "gl_merge_merge_request"),
     Description("Merge (accept/complete) a merge request. Optionally squash, remove the source branch, or set the merge commit message. Requires write mode.")]
    public static async Task<string> Merge(
        GitlabService svc,
        [Description("Merge request IID.")] long iid,
        [Description("Squash all commits into one when merging. Default false.")] bool squash = false,
        [Description("Remove the source branch after merging. Default false.")] bool removeSourceBranch = false,
        [Description("Optional custom merge commit message.")] string? mergeCommitMessage = null,
        [Description("Expected head SHA; the merge is rejected if the MR head has moved. Optional safety check.")] string? sha = null,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        EnsureMr(svc);
        svc.EnsureWriteAllowed("merge_merge_request");
        var (client, _) = await ResolveAsync(svc, project);

        var merge = new MergeRequestMerge
        {
            Squash = squash,
            ShouldRemoveSourceBranch = removeSourceBranch,
            MergeCommitMessage = TextUtil.NormalizeNewlines(mergeCommitMessage),
            Sha = sha,
        };
        var mr = client.Accept(iid, merge);
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new { mr.Iid, mr.State, mr.MergeCommitSha, mr.WebUrl }, JsonOpts.Default);
    }
}
