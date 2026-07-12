using System.ComponentModel;
using System.Text;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class PipelineTools
{
    [McpServerTool(Name = "gl_list_pipelines"),
     Description("List recent CI/CD pipelines for a project.")]
    public static async Task<string> ListPipelines(
        GitlabService svc,
        [Description("Optional ref name (branch or tag) to filter by.")] string? @ref = null,
        [Description("Optional status filter: running, pending, success, failed, canceled, skipped, manual.")] string? status = null,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnablePipelines) throw new InvalidOperationException("Pipeline tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var pipelines = svc.Client.GetPipelines(p.Id);

        IEnumerable<PipelineBasic> source = pipelines.All;
        if (!string.IsNullOrWhiteSpace(@ref) || !string.IsNullOrWhiteSpace(status))
        {
            var query = new PipelineQuery();
            if (!string.IsNullOrWhiteSpace(@ref)) query.Ref = @ref;
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<JobStatus>(status, true, out var s))
                query.Status = s;
            source = pipelines.Search(query);
        }

        var summary = source
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(b => new
            {
                b.Id,
                Status = b.Status.ToString(),
                b.Ref,
                Sha = b.Sha.ToString(),
                b.WebUrl,
                b.Source,
            });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_get_pipeline"),
     Description("Get a single pipeline by Id.")]
    public static async Task<string> GetPipeline(
        GitlabService svc,
        [Description("Pipeline Id.")] long id,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnablePipelines) throw new InvalidOperationException("Pipeline tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var pipeline = await svc.Client.GetPipelines(p.Id).GetByIdAsync(id);
        var summary = new
        {
            pipeline.Id,
            Status = pipeline.Status.ToString(),
            pipeline.Ref,
            Sha = pipeline.Sha.ToString(),
            pipeline.WebUrl,
            pipeline.CreatedAt,
            pipeline.UpdatedAt,
            pipeline.StartedAt,
            pipeline.FinishedAt,
            pipeline.Duration,
            pipeline.User,
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_list_pipeline_jobs"),
     Description("List jobs in a pipeline, with each job's stage, status, timing and failure reason. Use this to find which job failed, then fetch its log with gl_get_job_log.")]
    public static async Task<string> ListPipelineJobs(
        GitlabService svc,
        [Description("Pipeline Id.")] long pipelineId,
        [Description("Only return jobs that failed or were canceled. Handy for diagnosing a broken pipeline.")] bool onlyFailed = false,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnablePipelines) throw new InvalidOperationException("Pipeline tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);

        IEnumerable<Job> source = svc.Client.GetPipelines(p.Id).GetJobs(pipelineId);
        if (onlyFailed)
            source = source.Where(j => j.Status is JobStatus.Failed or JobStatus.Canceled);

        var jobs = source
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(j => new
            {
                j.Id,
                j.Name,
                j.Stage,
                Status = j.Status.ToString(),
                j.AllowFailure,
                j.FailureReason,
                j.Ref,
                j.WebUrl,
                j.CreatedAt,
                j.StartedAt,
                j.FinishedAt,
                j.Duration,
            });
        return JsonSerializer.Serialize(jobs, JsonOpts.Default);
    }

    [McpServerTool(Name = "gl_get_job_log"),
     Description("Fetch the plain-text trace (log) of a single CI/CD job. Output is truncated to maxBytes (default 200KB) to protect agent context.")]
    public static async Task<string> GetJobLog(
        GitlabService svc,
        [Description("Job Id (from gl_list_pipeline_jobs).")] long jobId,
        [Description("Max bytes to return; remainder is truncated (default 204800).")] int maxBytes = 204800,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnablePipelines) throw new InvalidOperationException("Pipeline tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);

        var url = $"projects/{p.Id}/jobs/{jobId}/trace";
        using var resp = await svc.Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"gl_get_job_log failed for job {jobId} in project '{path}': HTTP {(int)resp.StatusCode} {resp.StatusCode}. " +
                "Common causes: job id wrong, the job produced no trace yet, or the token lacks read access. " +
                $"Response body: {body}");
        }
        var log = await resp.Content.ReadAsStringAsync();
        return LogText.TruncateToBytes(log, maxBytes);
    }

    [McpServerTool(Name = "gl_trigger_pipeline"),
     Description("Trigger a new pipeline on a given ref. Requires write mode.")]
    public static async Task<string> TriggerPipeline(
        GitlabService svc,
        [Description("Branch or tag name to run the pipeline on.")] string @ref,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnablePipelines) throw new InvalidOperationException("Pipeline tools are disabled.");
        svc.EnsureWriteAllowed("trigger_pipeline");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var pipeline = svc.Client.GetPipelines(p.Id).Create(@ref);
        return JsonSerializer.Serialize(new { pipeline.Id, Status = pipeline.Status.ToString(), pipeline.WebUrl }, JsonOpts.Default);
    }
}

internal static class LogText
{
    /// <summary>Truncate <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes, appending a marker when clipped.</summary>
    public static string TruncateToBytes(string text, int maxBytes)
    {
        var limit = Math.Max(1, maxBytes);
        if (Encoding.UTF8.GetByteCount(text) <= limit) return text;

        var bytes = Encoding.UTF8.GetBytes(text);
        // Trim back to a valid UTF-8 boundary (avoid splitting a multi-byte sequence).
        var take = limit;
        while (take > 0 && (bytes[take] & 0xC0) == 0x80) take--;
        var clipped = Encoding.UTF8.GetString(bytes, 0, take);
        return clipped + $"\n\n[truncated at {maxBytes} bytes — fetch with a larger maxBytes for more]";
    }
}
