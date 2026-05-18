# GitlabMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **GitLab** (gitlab.com and self-hosted) over Streamable HTTP.

## Features

- HTTP MCP server using the Streamable HTTP transport.
- **Read-only mode by default** — write/delete tools stay disabled until explicitly enabled.
- Project / group allow/deny lists and per-feature toggles (issues / merge requests / repository / pipelines / releases / groups / snippets).
- Configuration via `GitlabMCPSharp.json`, environment variables, or command line.
- Serilog logging to console and rolling files (daily + 50 MB rollover, 14-file retention).
- Runs as a console app or as a Windows Service.

## Configuration

Configure via `GitlabMCPSharp.json` or environment variables. Environment variables win over JSON; in Docker, use the `GITLABMCP_` prefix and `__` for nested keys.

| Setting | Default | Description |
| --- | --- | --- |
| `Gitlab:ApiBaseUrl` | `https://gitlab.com` | Override for self-hosted GitLab (`https://gitlab.example.com`) |
| `Gitlab:PersonalAccessToken` | _(none)_ | PAT with scope `read_api` (or `api` for writes) |
| `Gitlab:OAuth2Token` | _(none)_ | Optional OAuth2 token used in place of a PAT |
| `Gitlab:DefaultNamespace` | _(none)_ | Default group used when tools omit one |
| `Gitlab:DefaultProject` | _(none)_ | Default project (`group/project`) used when tools omit one |
| `Gitlab:UserAgent` | `GitlabMCPSharp` | UA header sent to GitLab |
| `Gitlab:ReadOnly` | `true` | When `true`, all write/delete tools are disabled |
| `Gitlab:DefaultPageSize` | `30` | Page size for list operations (max 100) |
| `Gitlab:MaxPages` | `5` | Max pages traversed when paginating |
| `Gitlab:RequestTimeoutSeconds` | `100` | HTTP timeout |
| `Gitlab:AllowedProjects` | `[]` | Allow-list of `group/project`. Empty = no restriction |
| `Gitlab:BlockedProjects` | `[]` | Deny-list of `group/project` |
| `Gitlab:AllowedGroups` | `[]` | Allow-list of group full paths |
| `Gitlab:EnableIssues` / `EnableMergeRequests` / `EnableRepository` / `EnablePipelines` / `EnableReleases` / `EnableGroups` / `EnableSnippets` | `true` | Per-feature tool toggles |
| `Gitlab:AcceptInvalidCertificates` | `false` | Accept self-signed TLS certs (self-hosted only) |
| `Server:Host` | `localhost` | Host to bind |
| `Server:Port` | `5702` | HTTP port |
| `Server:Path` | `/mcp` | MCP endpoint path |
| `Server:WindowsServiceName` | `GitlabMCPSharp` | Service name when running under SCM |
| `Server:Password` | blank | Optional MCP endpoint password; blank disables password auth |

When `Server:Password` is set, MCP requests must provide the password as `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`.

Arrays use numeric indexes, for example `GITLABMCP_Gitlab__AllowedProjects__0=group/project`. Booleans use `true` or `false`.

## Running

```sh
dotnet run
```

Then point your MCP client at `http://localhost:5702/mcp`.

### Docker

Tagged releases publish a multi-architecture image for `linux/amd64` and `linux/arm64` to GitHub Container Registry:

```sh
docker run --rm -p 5702:5702 \
  -e GITLABMCP_Gitlab__ApiBaseUrl=https://gitlab.example.com \
  -e GITLABMCP_Gitlab__PersonalAccessToken=your-token \
  -e GITLABMCP_Gitlab__AllowedProjects__0=group/project \
  -e GITLABMCP_Server__Password=change-me \
  ghcr.io/wixely/gitlabmcpsharp:latest
```

Version tags such as `v1.2.3` also publish image tags like `v1.2.3`, `1.2.3`, and `1.2`. Read-only mode is on by default; set `GITLABMCP_Gitlab__ReadOnly=false` only when you want write tools available.

## Running as a Windows Service

The host detects when it's launched by the Service Control Manager and switches to service mode automatically (config and logs resolve from the executable directory, not the SCM's `C:\Windows\System32` working directory).

Publish, then register with `sc.exe` (run as Administrator):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\GitlabMCPSharp

sc.exe create GitlabMCPSharp `
    binPath= "C:\Services\GitlabMCPSharp\GitlabMCPSharp.exe" `
    start= auto `
    DisplayName= "GitLab MCP (C#)"
sc.exe description GitlabMCPSharp "MCP server for GitLab."
sc.exe start GitlabMCPSharp
```

Put credentials in `C:\Services\GitlabMCPSharp\GitlabMCPSharp.Local.json` (or set `GITLABMCP_Gitlab__PersonalAccessToken` as a machine-level env var) — never in `GitlabMCPSharp.json`, which is checked in.

To remove:

```powershell
sc.exe stop GitlabMCPSharp
sc.exe delete GitlabMCPSharp
```

Logs land in `<install-dir>\logs\gitlabmcp-*.log`.

## Read-only mode

Read-only is **on by default**. To enable write tools (e.g. `create_issue`, `trigger_pipeline`), set `Gitlab:ReadOnly=false`.

## Merge request review

Full MR review surface (gated by `Gitlab:EnableMergeRequests`):

- **View**: `list_merge_requests`, `get_merge_request`, `list_merge_request_changes` (per-file diff), `get_merge_request_diff` (concatenated unified diff), `get_merge_request_approval_state`, `list_merge_request_discussions`, `list_merge_request_notes`, `list_merge_request_pipelines`.
- **Decide**: `approve_merge_request`, `unapprove_merge_request` (per-user, via the raw API endpoint).
- **Discuss**: `add_merge_request_note` (conversation), `add_merge_request_discussion` (new thread).
- **Cancel**: `close_merge_request`; `reopen_merge_request` to undo.

All decide/discuss/cancel tools require `Gitlab:ReadOnly=false`.
