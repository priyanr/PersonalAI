using System.Text;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Sandbox.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace PersonalAI.Api.Controllers;

/// <summary>REST surface for the sandbox file browser UI. Every action reuses the exact FileOp wire
/// protocol the model's own `sandbox` tool already drives via ISandboxHostClient.FileOpAsync — this
/// controller just renders JSON/binary HTTP responses instead of a model-facing text summary. Unlike the
/// tool, these endpoints never create a sandbox that never existed for this owner
/// (SandboxProvisioningService.TryEnsureExistingAsync) — a browser opened for an owner with no sandbox
/// history has nothing to show, so it 404s instead of provisioning one from scratch.</summary>
[ApiController]
[Route("api/sandbox/{ownerId:guid}/files")]
public class SandboxFilesController(
    SandboxProvisioningService provisioning,
    ISandboxHostClient hostClient,
    IOptions<SandboxSettings> settings) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    [HttpGet]
    public async Task<IActionResult> List(Guid ownerId, [FromQuery] string? path, [FromQuery] bool recursive, CancellationToken ct)
    {
        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.List, string.IsNullOrWhiteSpace(path) ? "." : path, Recursive: recursive), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });
        return Ok(response.Entries ?? []);
    }

    /// <summary>Sniffs text-vs-binary server-side against the FULL, untruncated raw bytes (ReadRaw never
    /// truncates — it fails outright over the download cap instead) so a valid file cut off mid multi-byte
    /// UTF-8 character never falsely reads as binary.</summary>
    [HttpGet("content")]
    public async Task<IActionResult> GetContent(Guid ownerId, [FromQuery] string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { message = "'path' is required." });

        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.ReadRaw, path), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });

        var bytes = Convert.FromBase64String(response.ContentBase64 ?? "");
        return TryDecodeAsText(bytes, out var text)
            ? Ok(new SandboxFileContentDto(true, text, response.Truncated, response.TotalSize))
            : Ok(new SandboxFileContentDto(false, null, response.Truncated, response.TotalSize));
    }

    /// <summary>Full overwrite from the browser's plain textarea editor — deliberately Write, not the
    /// model's find/replace Edit op, since a free-form editor has no "old text" to match against. Passes
    /// MaxDownloadBytes as an explicit cap override so a file large enough to open for viewing (GetContent
    /// uses the same cap) is never too large to save back — Write's own default cap is the much smaller
    /// MaxFileTransferBytes, sized for the model's write_file tool, not this editor.</summary>
    [HttpPut("content")]
    public async Task<IActionResult> SaveContent(Guid ownerId, [FromBody] SandboxFileWriteRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Path))
            return BadRequest(new { message = "'path' is required." });

        var bytes = Encoding.UTF8.GetBytes(body.Content ?? "");
        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.Write, body.Path,
                ContentBase64: Convert.ToBase64String(bytes), MaxBytes: settings.Value.MaxDownloadBytes), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });
        return NoContent();
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download(Guid ownerId, [FromQuery] string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { message = "'path' is required." });

        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.ReadRaw, path), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });

        var bytes = Convert.FromBase64String(response.ContentBase64 ?? "");
        var fileName = Path.GetFileName(path.Replace('\\', '/'));
        if (!ContentTypeProvider.TryGetContentType(fileName, out var contentType))
            contentType = "application/octet-stream";
        return File(bytes, contentType, fileName);
    }

    [HttpPost("zip")]
    public async Task<IActionResult> Zip(Guid ownerId, [FromBody] SandboxZipRequest? body, CancellationToken ct)
    {
        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.Zip, ".", SelectedPaths: body?.Paths), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });

        var bytes = Convert.FromBase64String(response.ContentBase64 ?? "");
        return File(bytes, "application/zip", $"{ownerId}.zip");
    }

    /// <summary>Reuses FileOp.Write unchanged — already binary-safe on the wire, so no protocol change was
    /// needed for upload. Bound by MaxFileTransferBytes (the same cap Write always had), not the larger
    /// download/zip caps — a known v1 limitation for large uploads, not fixed here.</summary>
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(Guid ownerId, [FromForm] string path, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "A file is required." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var targetPath = CombineRelative(path, file.FileName);

        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.Write, targetPath, ContentBase64: Convert.ToBase64String(bytes)), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });
        return Ok(new { path = targetPath, size = bytes.LongLength });
    }

    [HttpPost("mkdir")]
    public async Task<IActionResult> Mkdir(Guid ownerId, [FromBody] SandboxMkdirRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Path))
            return BadRequest(new { message = "'path' is required." });

        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.MakeDirectory, body.Path), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });
        return NoContent();
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete(Guid ownerId, [FromBody] SandboxDeleteRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Path))
            return BadRequest(new { message = "'path' is required." });

        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.Delete, body.Path, Recursive: body.Recursive), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });
        return NoContent();
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move(Guid ownerId, [FromBody] SandboxMoveRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Path) || string.IsNullOrWhiteSpace(body.Destination))
            return BadRequest(new { message = "'path' and 'destination' are required." });

        var (response, error) = await RunFileOpAsync(ownerId, sandboxId =>
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.Move, body.Path, DestinationPath: body.Destination), ct);
        if (error is not null) return error;
        if (!response!.Success) return BadRequest(new { message = response.Error });
        return NoContent();
    }

    /// <summary>Ensures the owner's sandbox exists (404 if this owner has no browsable sandbox history —
    /// see TryEnsureExistingAsync), then runs one FileOp against it. 503, not a raw 500, if the broker
    /// disconnects between the "Sandbox files" button rendering (backed by the separate, non-side-effecting
    /// status check) and this actual request.</summary>
    private async Task<(FileOpResponse? Response, IActionResult? Error)> RunFileOpAsync(
        Guid ownerId, Func<Guid, FileOpRequest> buildRequest, CancellationToken ct)
    {
        var (found, sandboxId, _, _, provisionError) = await provisioning.TryEnsureExistingAsync(ownerId, ct);
        if (!found)
            return (null, NotFound(new { message = "No sandbox found for this owner." }));
        if (provisionError is not null)
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = provisionError }));

        try
        {
            var response = await hostClient.FileOpAsync(buildRequest(sandboxId), ct);
            return (response, null);
        }
        catch (SandboxHostDisconnectedException ex)
        {
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ex.Message }));
        }
        catch (TimeoutException)
        {
            // FileOpAsync shares Sandbox:RequestTimeoutSeconds with every other op — a large Zip/ReadRaw
            // payload can plausibly exceed it on slower hardware/links. Same 503 treatment as a dropped
            // connection: the broker not responding in time is the same class of failure from here.
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The sandbox host did not respond in time." }));
        }
    }

    private static string CombineRelative(string? directory, string fileName)
    {
        var cleanDirectory = (directory ?? "").Trim('/', '\\');
        var cleanName = Path.GetFileName(fileName);
        return string.IsNullOrEmpty(cleanDirectory) || cleanDirectory == "."
            ? cleanName
            : $"{cleanDirectory}/{cleanName}";
    }

    /// <summary>Strict decode: an embedded NUL or any byte sequence that isn't valid UTF-8 fails rather
    /// than silently mangling via replacement characters — the caller uses this to decide "safe to show as
    /// text" vs "offer download only", so a false positive here would render corrupted text in the
    /// browser.</summary>
    private static bool TryDecodeAsText(byte[] bytes, out string? text)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            text = null;
            return false;
        }

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = null;
            return false;
        }
    }
}
