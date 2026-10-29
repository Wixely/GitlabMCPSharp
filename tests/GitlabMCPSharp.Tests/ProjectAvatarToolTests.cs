using System.Net;
using System.Text;
using System.Text.Json;
using GitlabMCPSharp.Tools;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol;

namespace GitlabMCPSharp.Tests;

public sealed class ProjectAvatarToolTests
{
    private const string AvatarUrl = "https://gitlab.test/uploads/-/system/project/avatar/42/logo.png";

    // A complete 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9 };

    private static string ProjectJson(string? avatarUrl) =>
        JsonSerializer.Serialize(new { id = 42, path_with_namespace = "group/project", avatar_url = avatarUrl });

    /// <summary>Answers PUTs with <paramref name="putAvatar"/> and GETs with <paramref name="getAvatar"/>.</summary>
    private static FakeGitlabHandler Gitlab(string? putAvatar, string? getAvatar) => new(req =>
        req.Method == HttpMethod.Put
            ? FakeGitlabHandler.Json(HttpStatusCode.OK, ProjectJson(putAvatar))
            : FakeGitlabHandler.Json(HttpStatusCode.OK, ProjectJson(getAvatar)));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static async Task<List<(string? Disposition, string? ContentType, byte[] Body)>> ReadMultipart(RecordedRequest request)
    {
        Assert.Equal("multipart/form-data", request.MediaType);
        Assert.NotNull(request.Boundary);
        var reader = new MultipartReader(request.Boundary!, new MemoryStream(request.Body));
        var sections = new List<(string?, string?, byte[])>();
        while (await reader.ReadNextSectionAsync() is { } section)
        {
            using var copy = new MemoryStream();
            await section.Body.CopyToAsync(copy);
            sections.Add((section.ContentDisposition, section.ContentType, copy.ToArray()));
        }
        return sections;
    }

    // ------------------------------------------------------------------ upload

    [Fact]
    public async Task Upload_SendsMultipartPut_AndReturnsReadBackAvatarUrl()
    {
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler);

        var result = Parse(await ProjectTools.UpdateProjectAvatar(
            svc, image: Convert.ToBase64String(Png), fileName: "logo.png", project: "group/project"));

        Assert.Equal(2, handler.Requests.Count);
        var put = handler.Requests[0];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("https://gitlab.test/api/v4/projects/group%2Fproject", put.Uri.AbsoluteUri);

        var avatar = Assert.Single(await ReadMultipart(put));
        Assert.Contains("name=avatar", avatar.Disposition);
        Assert.Contains("filename=logo.png", avatar.Disposition);
        Assert.Equal("image/png", avatar.ContentType);
        Assert.Equal(Png, avatar.Body);

        var get = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, get.Method);
        Assert.Equal(put.Uri, get.Uri);

        Assert.Equal("group/project", result.GetProperty("Project").GetString());
        Assert.Equal("Uploaded", result.GetProperty("Action").GetString());
        Assert.Equal(AvatarUrl, result.GetProperty("AvatarUrl").GetString());
        Assert.True(result.GetProperty("Verified").GetBoolean());
        Assert.Equal("logo.png", result.GetProperty("FileName").GetString());
        Assert.Equal("image/png", result.GetProperty("MediaType").GetString());
        Assert.Equal(Png.Length, result.GetProperty("SizeBytes").GetInt32());
        Assert.False(result.TryGetProperty("Warning", out _));
    }

    [Fact]
    public async Task Upload_AcceptsDataUri_AndDefaultsFileNameAndProject()
    {
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler, o => o.DefaultProject = "team/default");

        var result = Parse(await ProjectTools.UpdateProjectAvatar(
            svc, image: "data:image/jpeg;base64," + Convert.ToBase64String(Jpeg)));

        var put = handler.Requests[0];
        Assert.Equal("https://gitlab.test/api/v4/projects/team%2Fdefault", put.Uri.AbsoluteUri);
        var avatar = Assert.Single(await ReadMultipart(put));
        Assert.Contains("filename=avatar.jpg", avatar.Disposition);
        Assert.Equal("image/jpeg", avatar.ContentType);
        Assert.Equal("avatar.jpg", result.GetProperty("FileName").GetString());
        Assert.Equal("team/default", result.GetProperty("Project").GetString());
    }

    [Fact]
    public async Task Upload_AcceptsMatchingMediaTypeAndWrappedBase64()
    {
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler);
        var wrapped = Convert.ToBase64String(Png, Base64FormattingOptions.InsertLineBreaks).Insert(10, "\n  ");

        var result = Parse(await ProjectTools.UpdateProjectAvatar(svc, image: wrapped, mediaType: "IMAGE/PNG"));

        Assert.Equal("avatar.png", result.GetProperty("FileName").GetString());
        Assert.Equal(Png, Assert.Single(await ReadMultipart(handler.Requests[0])).Body);
    }

    [Fact]
    public async Task Upload_StripsDirectoriesFromFileName()
    {
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler);

        var result = Parse(await ProjectTools.UpdateProjectAvatar(
            svc, image: Convert.ToBase64String(Png), fileName: @"..\..\etc/my logo.PNG"));

        Assert.Equal("my_logo.png", result.GetProperty("FileName").GetString());
    }

    [Fact]
    public async Task Upload_WhenReadBackFails_ReturnsWriteResponseUrlWithWarning()
    {
        var handler = new FakeGitlabHandler(req => req.Method == HttpMethod.Put
            ? FakeGitlabHandler.Json(HttpStatusCode.OK, ProjectJson(AvatarUrl))
            : FakeGitlabHandler.Json(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}"));
        var svc = FakeGitlabHandler.CreateService(handler);

        var result = Parse(await ProjectTools.UpdateProjectAvatar(svc, image: Convert.ToBase64String(Png)));

        Assert.Equal(AvatarUrl, result.GetProperty("AvatarUrl").GetString());
        Assert.False(result.GetProperty("Verified").GetBoolean());
        Assert.Contains("reading the project back failed", result.GetProperty("Warning").GetString());
    }

    [Fact]
    public async Task Upload_WhenReadBackHasNoAvatar_ReportsUnverified()
    {
        var handler = Gitlab(putAvatar: AvatarUrl, getAvatar: null);
        var svc = FakeGitlabHandler.CreateService(handler);

        var result = Parse(await ProjectTools.UpdateProjectAvatar(svc, image: Convert.ToBase64String(Png)));

        Assert.False(result.GetProperty("Verified").GetBoolean());
        Assert.Contains("reports no avatar_url", result.GetProperty("Warning").GetString());
    }

    // ------------------------------------------------------------------ remove

    [Fact]
    public async Task Remove_SendsBlankAvatarField_AndVerifiesItIsGone()
    {
        var handler = Gitlab(putAvatar: null, getAvatar: null);
        var svc = FakeGitlabHandler.CreateService(handler);

        var result = Parse(await ProjectTools.UpdateProjectAvatar(svc, remove: true, project: "group/project"));

        Assert.Equal(2, handler.Requests.Count);
        var put = handler.Requests[0];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("https://gitlab.test/api/v4/projects/group%2Fproject", put.Uri.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", put.MediaType);
        Assert.Equal("avatar=", put.BodyText);

        Assert.Equal("Removed", result.GetProperty("Action").GetString());
        Assert.False(result.TryGetProperty("AvatarUrl", out _));
        Assert.True(result.GetProperty("Verified").GetBoolean());
        Assert.False(result.TryGetProperty("FileName", out _));
    }

    [Fact]
    public async Task Remove_WhenAvatarStillPresent_ReportsUnverified()
    {
        var handler = Gitlab(putAvatar: null, getAvatar: AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler);

        var result = Parse(await ProjectTools.UpdateProjectAvatar(svc, remove: true));

        Assert.False(result.GetProperty("Verified").GetBoolean());
        Assert.Contains("still reports an avatar_url", result.GetProperty("Warning").GetString());
    }

    // --------------------------------------------------------- read-only mode

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyMode_BlocksBeforeAnyRequest(bool remove)
    {
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler, o => o.ReadOnly = true);

        var ex = await Assert.ThrowsAsync<McpException>(() => ProjectTools.UpdateProjectAvatar(
            svc, image: remove ? null : Convert.ToBase64String(Png), remove: remove));

        Assert.Contains("update_project_avatar", ex.Message);
        Assert.Contains("read-only", ex.Message);
        Assert.Empty(handler.Requests);
    }

    // ----------------------------------------------------- project restrictions

    [Fact]
    public async Task ProjectNotInAllowList_IsRejectedBeforeAnyRequest()
    {
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler, o => o.AllowedProjects.Add("group/other"));

        var ex = await Assert.ThrowsAsync<McpException>(() => ProjectTools.UpdateProjectAvatar(
            svc, image: Convert.ToBase64String(Png), project: "group/project"));

        Assert.Contains("AllowedProjects", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DefaultProjectOutsideAllowList_IsRejected()
    {
        var handler = Gitlab(null, null);
        var svc = FakeGitlabHandler.CreateService(handler, o => o.AllowedProjects.Add("group/other"));

        var ex = await Assert.ThrowsAsync<McpException>(() => ProjectTools.UpdateProjectAvatar(svc, remove: true));

        Assert.Contains("AllowedProjects", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BlockedProject_IsRejectedBeforeAnyRequest()
    {
        var handler = Gitlab(null, null);
        var svc = FakeGitlabHandler.CreateService(handler, o => o.BlockedProjects.Add("GROUP/Project"));

        var ex = await Assert.ThrowsAsync<McpException>(() => ProjectTools.UpdateProjectAvatar(
            svc, remove: true, project: "group/project"));

        Assert.Contains("BlockedProjects", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NoProjectAndNoDefault_IsRejected()
    {
        var handler = Gitlab(null, null);
        var svc = FakeGitlabHandler.CreateService(handler, o => o.DefaultProject = null);

        var ex = await Assert.ThrowsAsync<McpException>(() => ProjectTools.UpdateProjectAvatar(svc, remove: true));

        Assert.Contains("DefaultProject", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AllowListedProject_IsAccepted()
    {
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler, o => o.AllowedProjects.Add("Group/Project"));

        await ProjectTools.UpdateProjectAvatar(svc, image: Convert.ToBase64String(Png), project: "group/project");

        Assert.Equal(2, handler.Requests.Count);
    }

    // -------------------------------------------------------------- validation

    public static TheoryData<string?, string?, string?, bool, string> InvalidInputs()
    {
        var png = Convert.ToBase64String(Png);
        var tooBig = new byte[ProjectAvatar.MaxBytes + 1];
        Png.CopyTo(tooBig, 0);

        return new TheoryData<string?, string?, string?, bool, string>
        {
            // image, fileName, mediaType, remove, expected message fragment
            { null, null, null, false, "no image supplied" },
            { "   ", null, null, false, "no image supplied" },
            { png, null, null, true, "not both" },
            { "not base64!!", null, null, false, "not valid base64" },
            { Convert.ToBase64String(Encoding.UTF8.GetBytes("plain text, not an image")), null, null, false, "not a recognised image" },
            { Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")), "logo.svg", null, false, "not a recognised image" },
            { png, "logo.svg", null, false, "'.svg' is not a supported" },
            { png, "logo.jpg", null, false, "does not match the image content" },
            { png, "logo", null, false, "needs an image extension" },
            { png, null, "image/svg+xml", false, "'image/svg+xml' is not a supported" },
            { png, null, "image/gif", false, "does not match the image content" },
            { "data:image/gif;base64," + png, null, null, false, "data URI media type 'image/gif' does not match" },
            { "data:image/png," + png, null, null, false, "only base64 data URIs" },
            { "data:image/png;base64", null, null, false, "no ','" },
            { "data:image/png;base64,", null, null, false, "decoded image is empty" },
            { Convert.ToBase64String(tooBig), null, null, false, "200 KB" },
            { Convert.ToBase64String(new byte[1024 * 1024]), null, null, false, "200 KB" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task InvalidInput_IsRejectedBeforeAnyRequest(
        string? image, string? fileName, string? mediaType, bool remove, string expected)
    {
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler);

        var ex = await Assert.ThrowsAsync<McpException>(() => ProjectTools.UpdateProjectAvatar(
            svc, image: image, fileName: fileName, mediaType: mediaType, remove: remove));

        Assert.Contains(expected, ex.Message);
        Assert.StartsWith("gl_update_project_avatar", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ImageExactlyAtLimit_IsAccepted()
    {
        var bytes = new byte[ProjectAvatar.MaxBytes];
        Png.CopyTo(bytes, 0);
        var handler = Gitlab(AvatarUrl, AvatarUrl);
        var svc = FakeGitlabHandler.CreateService(handler);

        var result = Parse(await ProjectTools.UpdateProjectAvatar(svc, image: Convert.ToBase64String(bytes)));

        Assert.Equal(ProjectAvatar.MaxBytes, result.GetProperty("SizeBytes").GetInt32());
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "png", "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, "jpeg", "jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0 }, "gif", "gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "bmp", "bmp")]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0 }, "tiff", "tiff")]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0, 8 }, "tiff", "tiff")]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 }, "ico", "ico")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "webp", "webp")]
    public void SupportedFormats_AreDetected(byte[] bytes, string format, string extension)
    {
        var image = ProjectAvatar.Parse(Convert.ToBase64String(bytes), null, null, "t");

        Assert.Equal(format, image.Format.Name);
        Assert.Equal($"avatar.{extension}", image.FileName);
    }

    // ------------------------------------------------------------ API failures

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"message\":{\"avatar\":[\"is too big (should be at most 200 KB)\"]}}", "unsupported file type", "is too big")]
    [InlineData(HttpStatusCode.Unauthorized, "{\"message\":\"401 Unauthorized\"}", "missing, expired or invalid", "401 Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"403 Forbidden\"}", "Maintainer or Owner", "403 Forbidden")]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"404 Project Not Found\"}", "not found", "404 Project Not Found")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "", "too large", "HTTP 413")]
    [InlineData(HttpStatusCode.InternalServerError, "upstream exploded", "HTTP 500", "upstream exploded")]
    public async Task ApiFailure_SurfacesStatusHintAndGitlabMessage(
        HttpStatusCode status, string body, string hint, string gitlabDetail)
    {
        var handler = new FakeGitlabHandler(_ => FakeGitlabHandler.Json(status, body));
        var svc = FakeGitlabHandler.CreateService(handler);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            ProjectTools.UpdateProjectAvatar(svc, image: Convert.ToBase64String(Png), project: "group/project"));

        Assert.StartsWith("gl_update_project_avatar failed uploading an avatar to project 'group/project'", ex.Message);
        Assert.Contains($"HTTP {(int)status}", ex.Message);
        Assert.Contains(hint, ex.Message);
        Assert.Contains(gitlabDetail, ex.Message);
        Assert.DoesNotContain("test-token", ex.Message);
        Assert.Single(handler.Requests); // no readback after a failed write
    }

    [Fact]
    public async Task ApiFailure_OnRemove_SaysRemoving()
    {
        var handler = new FakeGitlabHandler(_ => FakeGitlabHandler.Json(HttpStatusCode.Forbidden, "{\"message\":\"403 Forbidden\"}"));
        var svc = FakeGitlabHandler.CreateService(handler);

        var ex = await Assert.ThrowsAsync<McpException>(() => ProjectTools.UpdateProjectAvatar(svc, remove: true));

        Assert.Contains("removing the avatar of project 'group/project'", ex.Message);
        Assert.Contains("403", ex.Message);
    }
}
