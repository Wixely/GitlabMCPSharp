using System.Net.Http.Headers;
using GitlabMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using NGitLab;

namespace GitlabMCPSharp.Services;

public sealed class GitlabService
{
    private readonly Lazy<IGitLabClient> _client;
    private readonly Lazy<HttpClient> _http;
    private readonly GitlabOptions _options;

    public GitlabService(IOptions<GitlabOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<IGitLabClient>(CreateClient);
        _http = new Lazy<HttpClient>(CreateHttpClient);
    }

    public GitlabOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;
    public IGitLabClient Client => _client.Value;

    /// <summary>HTTP client preconfigured with the API base URL and auth header, for endpoints NGitLab does not surface.</summary>
    public HttpClient Http => _http.Value;

    /// <summary>Resolve the project path, falling back to the default and applying allow/deny lists.</summary>
    public string ResolveProject(string? project)
    {
        var resolved = string.IsNullOrWhiteSpace(project) ? _options.DefaultProject : project;
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException("No project specified and Gitlab:DefaultProject is not configured.");

        if (_options.AllowedProjects.Count > 0 &&
            !_options.AllowedProjects.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Project '{resolved}' is not in the AllowedProjects list.");
        }
        if (_options.BlockedProjects.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Project '{resolved}' is in the BlockedProjects list.");
        }
        return resolved!;
    }

    public string ResolveGroup(string? group)
    {
        var resolved = string.IsNullOrWhiteSpace(group) ? _options.DefaultNamespace : group;
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException("No group specified and Gitlab:DefaultNamespace is not configured.");

        if (_options.AllowedGroups.Count > 0 &&
            !_options.AllowedGroups.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Group '{resolved}' is not in the AllowedGroups list.");
        }
        return resolved!;
    }

    public void EnsureWriteAllowed(string operation)
    {
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode. " +
                "Set Gitlab:ReadOnly=false to allow writes.");
        }
    }

    private HttpClient CreateHttpClient()
    {
        var baseUrl = _options.ApiBaseUrl.EndsWith('/') ? _options.ApiBaseUrl : _options.ApiBaseUrl + "/";
        var http = new HttpClient { BaseAddress = new Uri(new Uri(baseUrl), "api/v4/") };
        var token = !string.IsNullOrWhiteSpace(_options.OAuth2Token) ? _options.OAuth2Token : _options.PersonalAccessToken;
        if (!string.IsNullOrWhiteSpace(_options.OAuth2Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else
            http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private IGitLabClient CreateClient()
    {
        var token = !string.IsNullOrWhiteSpace(_options.OAuth2Token)
            ? _options.OAuth2Token
            : _options.PersonalAccessToken;

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "No GitLab token configured. Set Gitlab:PersonalAccessToken or Gitlab:OAuth2Token.");
        }

        return new GitLabClient(_options.ApiBaseUrl, token);
    }
}
