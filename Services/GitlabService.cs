using System.Net.Http.Headers;
using GitlabMCPSharp.Configuration;
using ModelContextProtocol;
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
            throw new McpException("No project specified and Gitlab:DefaultProject is not configured.");

        if (_options.AllowedProjects.Count > 0 &&
            !_options.AllowedProjects.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"Project '{resolved}' is not in the AllowedProjects list.");
        }
        if (_options.BlockedProjects.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"Project '{resolved}' is in the BlockedProjects list.");
        }
        return resolved!;
    }

    public string ResolveGroup(string? group)
    {
        var resolved = string.IsNullOrWhiteSpace(group) ? _options.DefaultNamespace : group;
        if (string.IsNullOrWhiteSpace(resolved))
            throw new McpException("No group specified and Gitlab:DefaultNamespace is not configured.");

        if (_options.AllowedGroups.Count > 0 &&
            !_options.AllowedGroups.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"Group '{resolved}' is not in the AllowedGroups list.");
        }
        return resolved!;
    }

    /// <summary>
    /// Apply the project allow/deny lists to a path that does not exist yet.
    ///
    /// <see cref="ResolveProject"/> assumes the project is already there and can fall back to
    /// <c>Gitlab:DefaultProject</c>; neither holds when creating one. Without this, configuring
    /// AllowedProjects would still leave creation unrestricted, which is the one way to get a
    /// project onto the instance that the list was meant to prevent.
    /// </summary>
    public string EnsureProjectPathAllowed(string pathWithNamespace)
    {
        if (string.IsNullOrWhiteSpace(pathWithNamespace))
            throw new McpException("No project path supplied.");

        if (_options.AllowedProjects.Count > 0 &&
            !_options.AllowedProjects.Contains(pathWithNamespace, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"Project '{pathWithNamespace}' is not in the AllowedProjects list, so it cannot be created. " +
                "Add it to Gitlab:AllowedProjects first, or clear the list to allow any project.");
        }
        if (_options.BlockedProjects.Contains(pathWithNamespace, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"Project '{pathWithNamespace}' is in the BlockedProjects list.");
        }
        return pathWithNamespace;
    }

    /// <summary>
    /// Apply the group allow-list to a group that is being targeted rather than resolved — the
    /// namespace a project is created in, or the group a project is shared with. Unlike
    /// <see cref="ResolveGroup"/> this never falls back to <c>Gitlab:DefaultNamespace</c>, because
    /// silently creating or sharing into a different group than the caller named would be worse
    /// than failing.
    /// </summary>
    public string EnsureGroupAllowed(string group)
    {
        if (string.IsNullOrWhiteSpace(group))
            throw new McpException("No group supplied.");

        if (_options.AllowedGroups.Count > 0 &&
            !_options.AllowedGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"Group '{group}' is not in the AllowedGroups list.");
        }
        return group;
    }

    public void EnsureWriteAllowed(string operation)
    {
        if (_options.ReadOnly)
        {
            throw new McpException(
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
            throw new McpException(
                "No GitLab token configured. Set Gitlab:PersonalAccessToken or Gitlab:OAuth2Token.");
        }

        return new GitLabClient(_options.ApiBaseUrl, token);
    }
}
