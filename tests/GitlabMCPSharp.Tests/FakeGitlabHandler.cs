using System.Net;
using System.Text;
using GitlabMCPSharp.Configuration;
using GitlabMCPSharp.Services;
using Microsoft.Extensions.Options;

namespace GitlabMCPSharp.Tests;

/// <summary>
/// Stands in for GitLab: records every request and answers from a caller-supplied responder, so
/// no test ever reaches a real instance.
/// </summary>
internal sealed class FakeGitlabHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _responder;

    public FakeGitlabHandler(Func<RecordedRequest, HttpResponseMessage> responder) => _responder = responder;

    public List<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content?.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "boundary")?.Value?.Trim('"'),
            body);
        Requests.Add(recorded);
        return _responder(recorded);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static GitlabService CreateService(FakeGitlabHandler handler, Action<GitlabOptions>? configure = null)
    {
        var options = new GitlabOptions
        {
            ApiBaseUrl = "https://gitlab.test",
            PersonalAccessToken = "test-token",
            ReadOnly = false,
            DefaultProject = "group/project",
        };
        configure?.Invoke(options);
        return new GitlabService(Options.Create(options), handler);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method, Uri Uri, string? MediaType, string? Boundary, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}
