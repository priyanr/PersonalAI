using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Controllers;

/// <summary>Every action here proxies to ISandboxHostClient.FileOpAsync through SandboxProvisioningService
/// — a real InMemory-backed provisioning service is used (mirroring SandboxToolTests/
/// SandboxHostControllerTests' own convention) rather than mocking it, since it's a sealed class with no
/// interface. Only ISandboxHostClient.FileOpAsync is mocked, so each test controls the wire response
/// directly without needing a real broker.</summary>
public class SandboxFilesControllerTests
{
    private sealed record Harness(SandboxFilesController Controller, ISandboxRepository Repository, Mock<ISandboxHostClient> HostClient);

    private static Harness NewHarness(SandboxSettings? settings = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ISandboxRepository, SandboxRepository>();
        var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<ISandboxRepository>();

        var hostClient = new Mock<ISandboxHostClient>();
        var provisioning = new SandboxProvisioningService(hostClient.Object, repository);
        var controller = new SandboxFilesController(provisioning, hostClient.Object, Options.Create(settings ?? new SandboxSettings()));

        return new Harness(controller, repository, hostClient);
    }

    private static async Task<Guid> SeedRunningSandboxAsync(ISandboxRepository repo)
    {
        var ownerId = Guid.NewGuid();
        await repo.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Running,
            PortsJson = "[]"
        }, CancellationToken.None);
        return ownerId;
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task List_NoSandboxForOwner_Returns404()
    {
        var h = NewHarness();

        var result = await h.Controller.List(Guid.NewGuid(), null, false, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task List_BrokerDisconnectsMidRequest_Returns503()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SandboxHostDisconnectedException("gone"));

        var result = await h.Controller.List(ownerId, null, false, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task List_Success_MapsToFileOpListWithDefaultPathAndRecursiveFlag()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null, Entries: [new FileEntry("a.txt", false, 3, DateTimeOffset.UtcNow)]));

        var result = await h.Controller.List(ownerId, "sub", true, CancellationToken.None);

        var entries = ((OkObjectResult)result).Value.Should().BeAssignableTo<IReadOnlyList<FileEntry>>().Subject;
        entries.Should().ContainSingle(e => e.Path == "a.txt");
        h.HostClient.Verify(c => c.FileOpAsync(
            It.Is<FileOpRequest>(r => r.Op == FileOp.List && r.Path == "sub" && r.Recursive), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetContent_TextFile_ReturnsIsTextTrueWithDecodedContent()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.Is<FileOpRequest>(r => r.Op == FileOp.ReadRaw), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null, ContentBase64: Base64("hello world"), TotalSize: 11));

        var result = await h.Controller.GetContent(ownerId, "notes.txt", CancellationToken.None);

        var dto = ((OkObjectResult)result).Value.Should().BeOfType<SandboxFileContentDto>().Subject;
        dto.IsText.Should().BeTrue();
        dto.Content.Should().Be("hello world");
        dto.TotalSize.Should().Be(11);
    }

    [Fact]
    public async Task GetContent_BinaryFile_ReturnsIsTextFalseWithNoContent()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        byte[] binary = [0x00, 0xFF, 0xD8, 0xFF, 0xE0]; // embedded NUL — not valid text under any decode
        h.HostClient
            .Setup(c => c.FileOpAsync(It.Is<FileOpRequest>(r => r.Op == FileOp.ReadRaw), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null, ContentBase64: Convert.ToBase64String(binary), TotalSize: binary.Length));

        var result = await h.Controller.GetContent(ownerId, "photo.jpg", CancellationToken.None);

        var dto = ((OkObjectResult)result).Value.Should().BeOfType<SandboxFileContentDto>().Subject;
        dto.IsText.Should().BeFalse();
        dto.Content.Should().BeNull();
    }

    [Fact]
    public async Task GetContent_MissingPath_ReturnsBadRequestWithoutCallingTheHost()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);

        var result = await h.Controller.GetContent(ownerId, null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        h.HostClient.Verify(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveContent_Success_CallsWriteWithUtf8EncodedContent()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null));

        var result = await h.Controller.SaveContent(ownerId, new SandboxFileWriteRequest("notes.txt", "updated text"), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        h.HostClient.Verify(c => c.FileOpAsync(
            It.Is<FileOpRequest>(r => r.Op == FileOp.Write && r.Path == "notes.txt" &&
                Encoding.UTF8.GetString(Convert.FromBase64String(r.ContentBase64!)) == "updated text"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveContent_PassesMaxDownloadBytesAsTheWriteCapOverride()
    {
        // A file large enough to open for viewing (GetContent uses MaxDownloadBytes) must not be too
        // large to save back — Write's own default cap is the much smaller MaxFileTransferBytes, sized
        // for the model's write_file tool, not this editor.
        var h = NewHarness(new SandboxSettings { MaxDownloadBytes = 999_000 });
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null));

        await h.Controller.SaveContent(ownerId, new SandboxFileWriteRequest("notes.txt", "updated text"), CancellationToken.None);

        h.HostClient.Verify(c => c.FileOpAsync(
            It.Is<FileOpRequest>(r => r.Op == FileOp.Write && r.MaxBytes == 999_000), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_RequestTimesOut_Returns503()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());

        var result = await h.Controller.List(ownerId, null, false, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Download_Success_ReturnsFileWithGuessedContentTypeAndFileName()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.Is<FileOpRequest>(r => r.Op == FileOp.ReadRaw), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null, ContentBase64: Base64("hi"), TotalSize: 2));

        var result = await h.Controller.Download(ownerId, "sub/notes.txt", CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be("notes.txt");
        file.ContentType.Should().Be("text/plain");
        file.FileContents.Should().BeEquivalentTo("hi"u8.ToArray());
    }

    [Fact]
    public async Task Download_UnrecognizedExtension_FallsBackToOctetStream()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.Is<FileOpRequest>(r => r.Op == FileOp.ReadRaw), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null, ContentBase64: Base64("x"), TotalSize: 1));

        var result = await h.Controller.Download(ownerId, "weirdfile.xyzabc", CancellationToken.None);

        result.Should().BeOfType<FileContentResult>().Which.ContentType.Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task Zip_Success_ReturnsZipFileNamedAfterOwner()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.Is<FileOpRequest>(r => r.Op == FileOp.Zip), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null, ContentBase64: Base64("pkzip-bytes")));

        var result = await h.Controller.Zip(ownerId, new SandboxZipRequest(["a.txt", "dir"]), CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be($"{ownerId}.zip");
        file.ContentType.Should().Be("application/zip");
        h.HostClient.Verify(c => c.FileOpAsync(
            It.Is<FileOpRequest>(r => r.Op == FileOp.Zip && r.SelectedPaths!.SequenceEqual(new[] { "a.txt", "dir" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Zip_HostReportsFailure_ReturnsBadRequestWithMessage()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.Is<FileOpRequest>(r => r.Op == FileOp.Zip), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), false, "selection too large"));

        var result = await h.Controller.Zip(ownerId, null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Upload_Success_WritesToTheTargetDirectoryAndReturnsOk()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null));

        var content = Encoding.UTF8.GetBytes("uploaded content");
        var formFile = new FormFile(new MemoryStream(content), 0, content.Length, "file", "report.txt");

        var result = await h.Controller.Upload(ownerId, "docs", formFile, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        h.HostClient.Verify(c => c.FileOpAsync(
            It.Is<FileOpRequest>(r => r.Op == FileOp.Write && r.Path == "docs/report.txt" &&
                Encoding.UTF8.GetString(Convert.FromBase64String(r.ContentBase64!)) == "uploaded content"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_EmptyFile_ReturnsBadRequestWithoutCallingTheHost()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        var formFile = new FormFile(new MemoryStream([]), 0, 0, "file", "empty.txt");

        var result = await h.Controller.Upload(ownerId, ".", formFile, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        h.HostClient.Verify(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Mkdir_Success_CallsMakeDirectory()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null));

        var result = await h.Controller.Mkdir(ownerId, new SandboxMkdirRequest("new/dir"), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        h.HostClient.Verify(c => c.FileOpAsync(
            It.Is<FileOpRequest>(r => r.Op == FileOp.MakeDirectory && r.Path == "new/dir"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_Success_CallsDeleteWithRecursiveFlag()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null));

        var result = await h.Controller.Delete(ownerId, new SandboxDeleteRequest("dir", true), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        h.HostClient.Verify(c => c.FileOpAsync(
            It.Is<FileOpRequest>(r => r.Op == FileOp.Delete && r.Path == "dir" && r.Recursive), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Move_Success_CallsMoveWithDestination()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);
        h.HostClient
            .Setup(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpResponse(Guid.NewGuid(), true, null));

        var result = await h.Controller.Move(ownerId, new SandboxMoveRequest("old.txt", "new.txt"), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        h.HostClient.Verify(c => c.FileOpAsync(
            It.Is<FileOpRequest>(r => r.Op == FileOp.Move && r.Path == "old.txt" && r.DestinationPath == "new.txt"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Move_MissingDestination_ReturnsBadRequestWithoutCallingTheHost()
    {
        var h = NewHarness();
        var ownerId = await SeedRunningSandboxAsync(h.Repository);

        var result = await h.Controller.Move(ownerId, new SandboxMoveRequest("old.txt", ""), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        h.HostClient.Verify(c => c.FileOpAsync(It.IsAny<FileOpRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
