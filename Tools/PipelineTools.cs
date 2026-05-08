using System.ComponentModel;
using System.Text.Json;
using GitlabMCPSharp.Services;
using ModelContextProtocol.Server;
using NGitLab;
using NGitLab.Models;

namespace GitlabMCPSharp.Tools;

[McpServerToolType]
public static class PipelineTools
{
    [McpServerTool(Name = "list_pipelines"),
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

    [McpServerTool(Name = "get_pipeline"),
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

    [McpServerTool(Name = "list_pipeline_jobs"),
     Description("List jobs in a pipeline.")]
    public static async Task<string> ListPipelineJobs(
        GitlabService svc,
        [Description("Pipeline Id.")] long pipelineId,
        [Description("Project namespaced path. Falls back to Gitlab:DefaultProject.")] string? project = null)
    {
        if (!svc.Options.EnablePipelines) throw new InvalidOperationException("Pipeline tools are disabled.");
        var path = svc.ResolveProject(project);
        var p = await svc.Client.Projects.GetByNamespacedPathAsync(path);
        var jobs = svc.Client.GetPipelines(p.Id).GetJobs(pipelineId)
            .Take(svc.Options.DefaultPageSize * svc.Options.MaxPages)
            .Select(j => new
            {
                j.Id,
                j.Name,
                j.Stage,
                Status = j.Status.ToString(),
                j.Ref,
                j.WebUrl,
                j.CreatedAt,
                j.StartedAt,
                j.FinishedAt,
                j.Duration,
            });
        return JsonSerializer.Serialize(jobs, JsonOpts.Default);
    }

    [McpServerTool(Name = "trigger_pipeline"),
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
