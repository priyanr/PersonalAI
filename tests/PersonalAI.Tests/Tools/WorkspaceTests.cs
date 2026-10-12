using FluentAssertions;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Tools;

public class WorkspaceTests : IDisposable
{
    private readonly string _root;
    private readonly Workspace _workspace;

    public WorkspaceTests()
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

    [Fact]
    public void Resolve_RelativePathInsideRoot_IsInsideWorkspace()
    {
        var result = _workspace.Resolve("subdir/file.txt");
        result.IsInsideWorkspace.Should().BeTrue();
        result.FullPath.Should().StartWith(_root);
    }

    [Fact]
    public void Resolve_TraversalOutsideRoot_IsNotInsideWorkspace()
    {
        var result = _workspace.Resolve("../outside.txt");
        result.IsInsideWorkspace.Should().BeFalse();
    }

    [Fact]
    public void Resolve_AbsolutePathOutsideRoot_IsNotInsideWorkspace()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), "some-other-dir", "file.txt");
        var result = _workspace.Resolve(outsidePath);
        result.IsInsideWorkspace.Should().BeFalse();
    }

    [Fact]
    public void Resolve_AbsolutePathInsideRoot_IsInsideWorkspace()
    {
        var insidePath = Path.Combine(_root, "nested", "file.txt");
        var result = _workspace.Resolve(insidePath);
        result.IsInsideWorkspace.Should().BeTrue();
    }

    [Fact]
    public void Resolve_EmptyPath_Throws()
    {
        var act = () => _workspace.Resolve("");
        act.Should().Throw<ArgumentException>();
    }

    // LooksLikeSandboxPath: the sandbox tool's own mount point (/work, a rooted Linux path inside its own
    // container) is never Workspace:RootPath in any deployment ("./data/workspace" locally, "/workspace"
    // in Docker/k8s) -- a model calling one of these workspace-confined tools with a /work-rooted path is
    // always the sandbox-vs-workspace mix-up this flag exists to catch, never a legitimate target.
    [Theory]
    [InlineData("/work")]
    [InlineData("/work/")]
    [InlineData("/work/script.py")]
    [InlineData("/work/nested/dir/file.txt")]
    [InlineData("/WORK/Script.PY")]
    public void Resolve_RootedWorkPath_LooksLikeSandboxPath(string path)
    {
        _workspace.Resolve(path).LooksLikeSandboxPath.Should().BeTrue();
    }

    [Theory]
    [InlineData("work/script.py")] // relative -- a genuine subdirectory named "work" inside THIS workspace is fine
    [InlineData("workspace/file.txt")] // shares a prefix with "work" but is a different word entirely
    [InlineData("/workbench/file.txt")] // same trap, rooted this time
    public void Resolve_PathsThatOnlyResembleWork_DoNotLooksLikeSandboxPath(string path)
    {
        _workspace.Resolve(path).LooksLikeSandboxPath.Should().BeFalse();
    }

    [Fact]
    public void Resolve_OrdinaryAbsolutePathOutsideRoot_DoesNotLooksLikeSandboxPath()
    {
        // Resolve_AbsolutePathOutsideRoot_IsNotInsideWorkspace's path must still go through the ordinary
        // approval flow, not the sandbox-specific hard refusal -- confirms the two checks don't overlap.
        var outsidePath = Path.Combine(Path.GetTempPath(), "some-other-dir", "file.txt");
        _workspace.Resolve(outsidePath).LooksLikeSandboxPath.Should().BeFalse();
    }
}
