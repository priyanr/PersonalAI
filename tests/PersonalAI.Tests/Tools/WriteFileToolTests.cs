using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Tools;

public class WriteFileToolTests : IDisposable
{
    private readonly string _root;
    private readonly Workspace _workspace;
    private readonly WriteFileTool _tool;

    public WriteFileToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "personalai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new Workspace(Options.Create(new WorkspaceSettings { RootPath = _root }));
        _tool = new WriteFileTool(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task ExecuteAsync_NewFileInsideWorkspace_CreatesFile()
    {
        var result = await _tool.ExecuteAsync(Args(new { path = "nested/new.txt", content = "hello" }), new ToolExecutionContext(false), CancellationToken.None);

        result.Should().BeOfType<ToolCompleted>();
        var full = Path.Combine(_root, "nested", "new.txt");
        File.Exists(full).Should().BeTrue();
        (await File.ReadAllTextAsync(full)).Should().Be("hello");
    }

    [Fact]
    public async Task ExecuteAsync_PathOutsideWorkspaceNotPreApproved_ReturnsApprovalRequiredAndDoesNotWrite()
    {
        var outsideFile = Path.Combine(Path.GetTempPath(), "personalai-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var result = await _tool.ExecuteAsync(Args(new { path = outsideFile, content = "x" }), new ToolExecutionContext(false), CancellationToken.None);

        result.Should().BeOfType<ToolApprovalRequired>();
        File.Exists(outsideFile).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PathOutsideWorkspacePreApproved_Writes()
    {
        var outsideFile = Path.Combine(Path.GetTempPath(), "personalai-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var result = await _tool.ExecuteAsync(Args(new { path = outsideFile, content = "approved" }), new ToolExecutionContext(true), CancellationToken.None);
            result.Should().BeOfType<ToolCompleted>();
            File.Exists(outsideFile).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(outsideFile)) File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingContent_Throws()
    {
        var act = async () => await _tool.ExecuteAsync(Args(new { path = "a.txt" }), new ToolExecutionContext(false), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_SandboxWorkPath_RefusesOutrightRatherThanRequestingApproval()
    {
        var result = await _tool.ExecuteAsync(Args(new { path = "/work/output.txt", content = "x" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("sandbox tool");
        File.Exists("/work/output.txt").Should().BeFalse();
    }
}
