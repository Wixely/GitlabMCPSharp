namespace GitlabMCPSharp.Configuration;

public sealed class GitlabOptions
{
    public const string SectionName = "Gitlab";

    /// <summary>Base URL for the GitLab instance. Defaults to gitlab.com; override for self-hosted GitLab.</summary>
    public string ApiBaseUrl { get; set; } = "https://gitlab.com";

    /// <summary>Personal Access Token. Required for authenticated requests. Needs at minimum 'read_api'; for writes 'api'.</summary>
    public string PersonalAccessToken { get; set; } = string.Empty;

    /// <summary>Optional OAuth2 access token used in place of a PAT.</summary>
    public string? OAuth2Token { get; set; }

    /// <summary>Default namespace (user or group path) used when tools are called without one.</summary>
    public string? DefaultNamespace { get; set; }

    /// <summary>Default project path used when tools are called without one (e.g. "my-group/my-project").</summary>
    public string? DefaultProject { get; set; }

    /// <summary>User-Agent header sent to GitLab.</summary>
    public string UserAgent { get; set; } = "GitlabMCPSharp";

    /// <summary>When true, all write/delete tools are disabled. Default true.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Maximum page size for list operations. GitLab caps at 100.</summary>
    public int DefaultPageSize { get; set; } = 30;

    /// <summary>Maximum number of pages to traverse for paginated list calls. Guards against runaway calls.</summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 100;

    /// <summary>Optional allow-list of project paths ("group/project"). Empty = no restriction.</summary>
    public List<string> AllowedProjects { get; set; } = new();

    /// <summary>Optional deny-list of project paths ("group/project"). Evaluated after AllowedProjects.</summary>
    public List<string> BlockedProjects { get; set; } = new();

    /// <summary>Optional allow-list of group paths. Empty = no restriction.</summary>
    public List<string> AllowedGroups { get; set; } = new();

    /// <summary>If true, expose tools that touch issues.</summary>
    public bool EnableIssues { get; set; } = true;

    /// <summary>If true, expose tools that touch merge requests.</summary>
    public bool EnableMergeRequests { get; set; } = true;

    /// <summary>If true, expose tools that touch repository contents (files, branches, commits).</summary>
    public bool EnableRepository { get; set; } = true;

    /// <summary>If true, expose tools that touch GitLab CI/CD pipelines and jobs.</summary>
    public bool EnablePipelines { get; set; } = true;

    /// <summary>If true, expose tools that touch releases and tags.</summary>
    public bool EnableReleases { get; set; } = true;

    /// <summary>If true, expose tools that touch groups and members.</summary>
    public bool EnableGroups { get; set; } = true;

    /// <summary>If true, expose snippet tools.</summary>
    public bool EnableSnippets { get; set; } = true;

    /// <summary>If true, accept self-signed TLS certificates (for self-hosted GitLab with internal CAs).</summary>
    public bool AcceptInvalidCertificates { get; set; } = false;
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5100;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "GitlabMCPSharp";
}
