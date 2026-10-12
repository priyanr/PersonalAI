using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Tools;

/// <summary>EditFileTool, ListFilesTool and SearchFilesTool have no dedicated test file of their own in
/// this repo (unlike ReadFileTool/WriteFileTool, which get their own sandbox-path case alongside their
/// existing tests) -- this covers just the one cross-cutting behaviour all five in-process file tools
/// share: a /work-rooted path (the sandbox tool's mount point, never Workspace:RootPath in any deployment)
/// is refused outright with a pointer to the sandbox tool, instead of pausing for a human to approve
/// something that's always wrong. The shared detection/message logic itself is covered by
/// WorkspaceTests; this is only "did each tool wire the check in correctly."</summary>
public class SandboxPathHintTests : IDisposable
{
    private readonly string _root;
    private readonly Workspace _workspace;

    public SandboxPathHintTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "personalai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new Workspace(Options.Create(new WorkspaceSettings { RootPath = _root }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task EditFileTool_SandboxWorkPath_RefusesOutrightRatherThanRequestingApproval()
    {
        var tool = new EditFileTool(_workspace);

        var result = await tool.ExecuteAsync(
            Args(new { path = "/work/script.py", old_string = "a", new_string = "b" }),
            new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("sandbox tool");
    }

    [Fact]
    public async Task ListFilesTool_SandboxWorkPath_RefusesOutrightRatherThanRequestingApproval()
    {
        var tool = new ListFilesTool(_workspace);

        var result = await tool.ExecuteAsync(
            Args(new { path = "/work" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("sandbox tool");
    }

    [Fact]
    public async Task SearchFilesTool_SandboxWorkPath_RefusesOutrightRatherThanRequestingApproval()
    {
        var tool = new SearchFilesTool(_workspace);

        var result = await tool.ExecuteAsync(
            Args(new { pattern = "TODO", path = "/work" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("sandbox tool");
    }
}
