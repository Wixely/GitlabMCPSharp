using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class MentionsTools
{
    [McpServerTool(Name = "list_mentions_since"),
     Description("Find recent issues, MRs and notes in a GitLab project where a given substring appears (typically a user/group mention such as \"@bot\" or any other phrase). Designed for polling-style consumers - returns a stable JSON shape with the match kind, the author, the body, the URL and timestamps.")]
    public static async Task<string> ListMentionsSince(
        GitlabService svc,
        [Description("Substring to search for in issue/MR/note bodies. Required. Case-insensitive.")] string mention,
        [Description("Project id or path-with-namespace (e.g. \"group/project\"). Falls back to Gitlab:DefaultProject if set.")] string? project = null,
        [Description("ISO-8601 UTC timestamp. Only return matches updated after this. Omit for no lower bound.")] string? sinceUtc = null,
        [Description("Include closed issues/MRs. Default false.")] bool includeClosed = false,
        [Description("Max matches returned across all kinds. Default 50, hard cap 200.")] int limit = 50)
    {
        var polledAt = DateTimeOffset.UtcNow;
        try
        {
            if (string.IsNullOrWhiteSpace(mention))
                throw new ArgumentException("mention must not be empty.", nameof(mention));

            var cap = Math.Clamp(limit, 1, 200);

            DateTime? since = null;
            if (!string.IsNullOrWhiteSpace(sinceUtc))
            {
                if (!DateTime.TryParse(sinceUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    throw new ArgumentException($"sinceUtc is not a valid ISO-8601 timestamp: '{sinceUtc}'.", nameof(sinceUtc));
                since = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            var path = svc.ResolveProject(project);
            var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
            var projectPath = p.PathWithNamespace ?? path;

            var matches = new List<MentionMatch>();

            // -------- Issues + their notes --------
            if (svc.Options.EnableIssues)
            {
                var issueQuery = new IssueQuery
                {
                    State = includeClosed ? null : IssueState.opened,
                    PerPage = svc.Options.DefaultPageSize,
                    UpdatedAfter = since,
                };

                var issues = svc.Client.Issues.Get(p.Id, issueQuery)
                    .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
                    .ToList();

                foreach (var issue in issues)
                {
                    if (ContainsMention(issue.Title, mention) || ContainsMention(issue.Description, mention))
                    {
                        matches.Add(new MentionMatch(
                            Kind: "issue",
                            Project: projectPath,
                            Iid: issue.IssueId,
                            NoteId: null,
                            Author: issue.Author?.Username,
                            Body: issue.Description ?? issue.Title,
                            Url: issue.WebUrl,
                            CreatedAt: ToUtc(issue.CreatedAt),
                            UpdatedAt: ToUtc(issue.UpdatedAt)));
                    }

                    // Scan notes on this issue (only worth doing if the issue itself was touched after `since`).
                    IEnumerable<ProjectIssueNote> notes;
                    try
                    {
                        notes = svc.Client.GetProjectIssueNoteClient(p.Id).ForIssue(issue.IssueId);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var note in notes)
                    {
                        if (since.HasValue && ToUtc(note.UpdatedAt) <= since.Value) continue;
                        if (!ContainsMention(note.Body, mention)) continue;

                        matches.Add(new MentionMatch(
                            Kind: "issue_note",
                            Project: projectPath,
                            Iid: issue.IssueId,
                            NoteId: note.NoteId,
                            Author: note.Author?.Username,
                            Body: note.Body,
                            Url: BuildNoteUrl(issue.WebUrl, note.NoteId),
                            CreatedAt: ToUtc(note.CreatedAt),
                            UpdatedAt: ToUtc(note.UpdatedAt)));
                    }
                }
            }

            // -------- Merge requests + their notes --------
            if (svc.Options.EnableMergeRequests)
            {
                var mrClient = svc.Client.GetMergeRequest(p.Id);

                var mrQuery = new MergeRequestQuery
                {
                    State = includeClosed ? null : MergeRequestState.opened,
                    PerPage = svc.Options.DefaultPageSize,
                    UpdatedAfter = since,
                };

                var mrs = mrClient.Get(mrQuery)
                    .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
                    .ToList();

                foreach (var mr in mrs)
                {
                    if (ContainsMention(mr.Title, mention) || ContainsMention(mr.Description, mention))
                    {
                        matches.Add(new MentionMatch(
                            Kind: "merge_request",
                            Project: projectPath,
                            Iid: mr.Iid,
                            NoteId: null,
                            Author: mr.Author?.Username,
                            Body: mr.Description ?? mr.Title,
                            Url: mr.WebUrl,
                            CreatedAt: ToUtc(mr.CreatedAt),
                            UpdatedAt: ToUtc(mr.UpdatedAt)));
                    }

                    IEnumerable<MergeRequestComment> comments;
                    try
                    {
                        comments = mrClient.Comments(mr.Iid).All;
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var comment in comments)
                    {
                        if (since.HasValue && ToUtc(comment.UpdatedAt) <= since.Value) continue;
                        if (!ContainsMention(comment.Body, mention)) continue;

                        matches.Add(new MentionMatch(
                            Kind: "merge_request_note",
                            Project: projectPath,
                            Iid: mr.Iid,
                            NoteId: comment.Id,
                            Author: comment.Author?.Username,
                            Body: comment.Body,
                            Url: BuildNoteUrl(mr.WebUrl, comment.Id),
                            CreatedAt: ToUtc(comment.CreatedAt),
                            UpdatedAt: ToUtc(comment.UpdatedAt)));
                    }
                }
            }

            var ordered = matches
                .OrderByDescending(m => m.UpdatedAt)
                .Take(cap)
                .ToList();

            var payload = new
            {
                matches = ordered,
                polledAt = polledAt.UtcDateTime,
                since = since,
                mention,
            };
            return JsonSerializer.Serialize(payload, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            var error = new
            {
                error = ex.Message,
                errorType = ex.GetType().Name,
                polledAt = polledAt.UtcDateTime,
                mention,
            };
            return JsonSerializer.Serialize(error, JsonOpts.Default);
        }
    }

    private static bool ContainsMention(string? body, string mention) =>
        !string.IsNullOrEmpty(body) && body.Contains(mention, StringComparison.OrdinalIgnoreCase);

    private static DateTime ToUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt :
        dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() :
        DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private static string? BuildNoteUrl(string? parentWebUrl, long noteId)
    {
        if (string.IsNullOrEmpty(parentWebUrl)) return null;
        return $"{parentWebUrl}#note_{noteId}";
    }

    private sealed record MentionMatch(
        string Kind,
        string Project,
        long Iid,
        long? NoteId,
        string? Author,
        string? Body,
        string? Url,
        DateTime CreatedAt,
        DateTime UpdatedAt);
}
