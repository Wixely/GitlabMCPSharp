using System.Text;
using ModelContextProtocol;

namespace GitlabMCPSharp.Tools;

/// <summary>
/// Decoding and validation for project avatar uploads.
///
/// Image content arrives inline (base64 or a data URI) rather than as a server-side path, so the
/// tool can never be used to read arbitrary files off the machine the MCP server runs on.
/// Everything GitLab would reject is caught here first, before any request is sent.
/// </summary>
internal static class ProjectAvatar
{
    /// <summary>
    /// GitLab's documented avatar limit: 200 KB, enforced server-side as <c>200.kilobytes</c>
    /// (204,800 bytes).
    /// </summary>
    public const int MaxBytes = 200 * 1024;

    /// <summary>
    /// Formats GitLab's avatar uploader accepts (<c>Gitlab::FileTypeDetection::SAFE_IMAGE_EXT</c>):
    /// png, jpg, jpeg, gif, bmp, tiff, ico, webp. GitLab decides by file extension, so the
    /// extension we send must agree with the bytes.
    /// </summary>
    public static readonly IReadOnlyList<ImageFormat> Formats = new[]
    {
        new ImageFormat("png", "image/png", new[] { "png" }),
        new ImageFormat("jpeg", "image/jpeg", new[] { "jpg", "jpeg" }, new[] { "image/jpg", "image/pjpeg" }),
        new ImageFormat("gif", "image/gif", new[] { "gif" }),
        new ImageFormat("bmp", "image/bmp", new[] { "bmp" }, new[] { "image/x-bmp", "image/x-ms-bmp" }),
        new ImageFormat("tiff", "image/tiff", new[] { "tiff" }),
        new ImageFormat("ico", "image/x-icon", new[] { "ico" }, new[] { "image/vnd.microsoft.icon", "image/ico" }),
        new ImageFormat("webp", "image/webp", new[] { "webp" }),
    };

    public static string SupportedList => "png, jpg/jpeg, gif, bmp, tiff, ico, webp";

    public sealed record ImageFormat(
        string Name, string MediaType, string[] Extensions, string[]? MediaTypeAliases = null)
    {
        public string DefaultExtension => Extensions[0];

        public bool MatchesMediaType(string mediaType) =>
            string.Equals(MediaType, mediaType, StringComparison.OrdinalIgnoreCase) ||
            (MediaTypeAliases?.Contains(mediaType, StringComparer.OrdinalIgnoreCase) ?? false);

        public bool MatchesExtension(string extension) =>
            Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public sealed record Image(byte[] Bytes, ImageFormat Format, string FileName);

    /// <summary>
    /// Decode and validate an avatar image. Throws <see cref="McpException"/> with a message that
    /// says what was wrong and how to fix it.
    /// </summary>
    public static Image Parse(string image, string? fileName, string? mediaType, string tool)
    {
        if (string.IsNullOrWhiteSpace(image))
            throw new McpException($"{tool}: image content is empty. Pass the image as base64 or a data URI, or set remove=true.");

        var payload = image.Trim();
        string? dataUriMediaType = null;

        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = payload.IndexOf(',');
            if (comma < 0)
                throw new McpException($"{tool}: the data URI has no ',' separating its header from the data.");

            var header = payload[5..comma];
            var parts = header.Split(';', StringSplitOptions.TrimEntries);
            if (!parts.Skip(1).Contains("base64", StringComparer.OrdinalIgnoreCase))
                throw new McpException($"{tool}: only base64 data URIs are supported (expected 'data:<media type>;base64,...').");

            dataUriMediaType = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0];
            payload = payload[(comma + 1)..];
        }

        var bytes = DecodeBase64(payload, tool);

        if (bytes.Length == 0)
            throw new McpException($"{tool}: the decoded image is empty.");
        if (bytes.Length > MaxBytes)
            throw new McpException(
                $"{tool}: the image is {bytes.Length:N0} bytes, over GitLab's avatar limit of {MaxBytes:N0} bytes (200 KB). " +
                "Resize or recompress it; GitLab recommends 192 x 192 pixels.");

        var detected = Detect(bytes)
            ?? throw new McpException(
                $"{tool}: the content is not a recognised image. Supported formats: {SupportedList}.");

        // Every declared type must agree with the bytes; GitLab trusts the extension, so a
        // mismatch would either be rejected or stored under the wrong type.
        CheckMediaType(dataUriMediaType, detected, "data URI media type", tool);
        CheckMediaType(mediaType, detected, "mediaType", tool);

        var name = BuildFileName(fileName, detected, tool);
        return new Image(bytes, detected, name);
    }

    private static byte[] DecodeBase64(string payload, string tool)
    {
        var sb = new StringBuilder(payload.Length);
        foreach (var c in payload)
        {
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        }
        var cleaned = sb.ToString();

        // Refuse oversized input before allocating the decoded buffer.
        var maxEncoded = (MaxBytes + 2) / 3 * 4;
        if (cleaned.Length > maxEncoded)
        {
            throw new McpException(
                $"{tool}: the image is larger than GitLab's avatar limit of {MaxBytes:N0} bytes (200 KB) " +
                $"(about {cleaned.Length / 4L * 3:N0} bytes encoded). Resize or recompress it; GitLab recommends 192 x 192 pixels.");
        }

        var buffer = new byte[cleaned.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(cleaned, buffer, out var written))
            throw new McpException($"{tool}: the image content is not valid base64.");

        return buffer[..written];
    }

    private static void CheckMediaType(string? declared, ImageFormat detected, string label, string tool)
    {
        if (string.IsNullOrWhiteSpace(declared)) return;
        var value = declared.Trim();

        var known = Formats.FirstOrDefault(f => f.MatchesMediaType(value));
        if (known is null)
        {
            throw new McpException(
                $"{tool}: {label} '{value}' is not a supported avatar type. Supported formats: {SupportedList}.");
        }
        if (!ReferenceEquals(known, detected))
        {
            throw new McpException(
                $"{tool}: {label} '{value}' does not match the image content, which is {detected.Name} ({detected.MediaType}).");
        }
    }

    private static string BuildFileName(string? fileName, ImageFormat detected, string tool)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return $"avatar.{detected.DefaultExtension}";

        // Only the leaf name is meaningful to GitLab; never forward a path.
        var leaf = fileName.Trim().Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];

        var dot = leaf.LastIndexOf('.');
        if (dot <= 0 || dot == leaf.Length - 1)
        {
            throw new McpException(
                $"{tool}: fileName '{fileName}' needs an image extension ({SupportedList}); GitLab uses it to decide the file type.");
        }

        var extension = leaf[(dot + 1)..];
        var known = Formats.FirstOrDefault(f => f.MatchesExtension(extension));
        if (known is null)
        {
            throw new McpException(
                $"{tool}: fileName extension '.{extension}' is not a supported avatar type. Supported formats: {SupportedList}.");
        }
        if (!ReferenceEquals(known, detected))
        {
            throw new McpException(
                $"{tool}: fileName extension '.{extension}' does not match the image content, which is {detected.Name} ({detected.MediaType}).");
        }

        var stem = Sanitize(leaf[..dot]);
        return $"{(stem.Length == 0 ? "avatar" : stem)}.{extension.ToLowerInvariant()}";
    }

    /// <summary>Keep the multipart filename to a conservative ASCII set.</summary>
    private static string Sanitize(string stem)
    {
        var sb = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        var result = sb.ToString().Trim('.');
        return result.Length > 100 ? result[..100] : result;
    }

    /// <summary>Identify the image format from its leading bytes.</summary>
    internal static ImageFormat? Detect(ReadOnlySpan<byte> b)
    {
        static ImageFormat F(string name) => Formats.First(f => f.Name == name);

        if (b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return F("png");
        if (b.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return F("jpeg");
        if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8)) return F("gif");
        if (b.Length >= 14 && b.StartsWith("BM"u8)) return F("bmp");
        if (b.StartsWith(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) || b.StartsWith(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })) return F("tiff");
        if (b.Length >= 6 && b.StartsWith(new byte[] { 0x00, 0x00, 0x01, 0x00 }) && (b[4] | b[5]) != 0) return F("ico");
        if (b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return F("webp");
        return null;
    }
}
