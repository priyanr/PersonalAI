using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Tools;

public class ReadFileToolTests : IDisposable
{
    private readonly string _root;
    private readonly Workspace _workspace;
    private readonly ReadFileTool _tool;

    public ReadFileToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "personalai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new Workspace(Options.Create(new WorkspaceSettings { RootPath = _root }));
        _tool = new ReadFileTool(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task ExecuteAsync_FileInsideWorkspace_ReturnsContent()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "line1\nline2\nline3");

        var result = await _tool.ExecuteAsync(Args(new { path = "a.txt" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        completed.Content.Should().Contain("line1").And.Contain("line2").And.Contain("line3");
    }

    [Fact]
    public async Task ExecuteAsync_PathOutsideWorkspaceNotPreApproved_ReturnsApprovalRequired()
    {
        var outsideFile = Path.Combine(Path.GetTempPath(), "personalai-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsideFile, "secret");
        try
        {
            var result = await _tool.ExecuteAsync(Args(new { path = outsideFile }), new ToolExecutionContext(false), CancellationToken.None);
            result.Should().BeOfType<ToolApprovalRequired>();
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PathOutsideWorkspacePreApproved_ReadsFile()
    {
        var outsideFile = Path.Combine(Path.GetTempPath(), "personalai-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsideFile, "secret content");
        try
        {
            var result = await _tool.ExecuteAsync(Args(new { path = outsideFile }), new ToolExecutionContext(true), CancellationToken.None);
            var completed = result.Should().BeOfType<ToolCompleted>().Subject;
            completed.Content.Should().Contain("secret content");
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingPath_Throws()
    {
        var act = async () => await _tool.ExecuteAsync(Args(new { }), new ToolExecutionContext(false), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_FileDoesNotExist_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(Args(new { path = "missing.txt" }), new ToolExecutionContext(false), CancellationToken.None);
        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_SandboxWorkPath_RefusesOutrightRatherThanRequestingApproval()
    {
        // The bug this guards against: a model that's used the sandbox tool internalizes "paths are
        // relative to /work" and sometimes calls THIS workspace-confined tool by the same action name
        // with a /work-rooted path -- that path can never legitimately land here (Workspace:RootPath is
        // never "/work"), so it must be refused immediately with a pointer to the right tool, not paused
        // for a human to approve something that's always wrong.
        var result = await _tool.ExecuteAsync(Args(new { path = "/work/script.py" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("sandbox tool");
    }
}
