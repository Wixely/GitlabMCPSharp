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

Read-only is **on by default**. To enable write tools (e.g. `gl_create_issue`, `gl_trigger_pipeline`), set `Gitlab:ReadOnly=false`.

## Project metadata

- `gl_get_project` returns project metadata, including its current description.
- `gl_update_project_description` replaces the short project description and returns the project
  path and applied value. Pass an empty string to clear it. The tool requires
  `Gitlab:ReadOnly=false`, an API-scoped token, and Maintainer or Owner access to the project.

## Creating and sharing projects

- `gl_create_project` creates a project. **Visibility defaults to `private` and is always sent
  explicitly**, so the instance default can never quietly make a new repository public. Give a
  `namespace` to create inside a group (resolved to an exact full-path match — it will never fall
  back to a different namespace); omit it for your personal namespace. If a project already exists
  at that `namespace/path` it is returned unchanged, so retries are safe.
- `gl_share_project_with_group` grants a group access to a project. Levels: `guest`, `planner`,
  `reporter`, `security_manager`, `developer`, `maintainer`, `owner`. It is idempotent — an equal
  or higher existing share is left alone and reported as `changed: false`.

- `gl_list_project_group_shares` reads back which groups a project is shared with, at what level,
  when the share expires, and whether it is `direct` or `inherited` from an ancestor group. This is
  the verification step: it reads the project's live state rather than trusting what a previous
  write returned. It is **read-only and works with `Gitlab:ReadOnly=true`**, so you can audit
  sharing without enabling writes at all.

The two write tools require `Gitlab:ReadOnly=false`, and both respect `Gitlab:AllowedProjects` /
`Gitlab:BlockedProjects` / `Gitlab:AllowedGroups`: a project you are not allowed to touch is also a
project you cannot create, and a group you are not allowed to touch is one you cannot share with.

`gl_list_project_group_shares` still honours `AllowedProjects` for the project itself, but it
reports **every** group a project is shared with, including groups outside `AllowedGroups`. Hiding
those would make it useless for the thing it exists to do — telling you the truth about who can
reach a repository. The optional `group` argument is a filter over that answer, not a permission
check, and the unfiltered total is always reported alongside it.

> **Raising an existing share is deliberately not supported.** GitLab's API offers only
> create (`POST /projects/:id/share`) and remove (`DELETE /projects/:id/share/:group_id`) — there
> is no in-place update — so increasing a group's level means deleting the share and re-adding it.
> This server has no tool that removes a share, so it reports a conflict instead of silently
> destroying and recreating the link. Change it in the GitLab UI.
>
> There is no project-deletion or group-unshare tool, by design.

## Merge request review

Full MR review surface (gated by `Gitlab:EnableMergeRequests`):

- **View**: `gl_list_merge_requests`, `gl_get_merge_request`, `gl_list_merge_request_changes` (per-file diff), `gl_get_merge_request_diff` (concatenated unified diff), `gl_get_merge_request_approval_state`, `gl_list_merge_request_discussions`, `gl_list_merge_request_notes`, `gl_list_merge_request_pipelines`.
- **Create**: `gl_create_merge_request` (title, source → target branch, optional description, `draft`, `removeSourceBranch`).
- **Request review**: `gl_request_merge_request_reviewers` (usernames or numeric user ids; replaces the reviewer set).
- **Decide**: `gl_approve_merge_request`, `gl_unapprove_merge_request` (per-user, via the raw API endpoint).
- **Discuss**: `gl_add_merge_request_note` (conversation), `gl_add_merge_request_discussion` (new thread), `gl_add_merge_request_review_comment` (inline, anchored to a file + `line`/`side` in the diff; markdown supported).
- **Complete**: `gl_merge_merge_request` (optional `squash`, `removeSourceBranch`, merge commit message).
- **Cancel**: `gl_close_merge_request`; `gl_reopen_merge_request` to undo.

All create/decide/discuss/complete/cancel tools require `Gitlab:ReadOnly=false`.

> **Line breaks:** MR descriptions and comment bodies accept markdown. If a caller sends literal `\n` escape sequences instead of real line breaks (a common mistake) and the text has no real newlines, the server converts them to actual line breaks so the content renders correctly. Text that already contains real newlines is left untouched.

The create → review → comment → approve → complete lifecycle is unified across the GitHub, Azure DevOps and GitLab MCP servers (see each server's README).

## Pipelines / CI

Pipeline tools (gated by `Gitlab:EnablePipelines`) let you diagnose a failing pipeline down to the individual job:

- **Pipelines**: `gl_list_pipelines`, `gl_get_pipeline`.
- **Per-job**: `gl_list_pipeline_jobs` lists each job with its stage, status, timing, `allow_failure` and `failure_reason` (pass `onlyFailed=true` to narrow to the jobs that broke); `gl_get_job_log` fetches a single job's trace (log), truncated to `maxBytes` (default 200 KB) to protect agent context.
- **Trigger**: `gl_trigger_pipeline` (requires `Gitlab:ReadOnly=false`).

Typical flow: `gl_list_pipelines` → `gl_list_pipeline_jobs pipelineId onlyFailed=true` → `gl_get_job_log jobId`. This mirrors the per-job log flow in the Azure DevOps and GitHub MCP servers.
