using FluentAssertions;
using PersonalAI.SandboxHost;

namespace PersonalAI.Tests.SandboxHost;

/// <summary>Mirrors PersonalAI.Tests.Tools.WorkspaceTests, which covers the same escape-resistance shape
/// for the in-process file tools' Workspace.Resolve. The sandbox variant is stricter: a rooted/absolute
/// path is rejected outright rather than merely flagged "outside" for a human to approve, because there is
/// no approval flow for the sandbox.</summary>
public class SandboxPathResolverTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly SandboxPathResolver _resolver;

    public SandboxPathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "personalai-sandboxhost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _resolver = new SandboxPathResolver(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // Symbolic-link escapes. These are the ones a lexical Path.GetFullPath check cannot catch: the model
    // can run `ln -s / /work/esc` inside its own container (root, no dropped capabilities by default), and
    // the resulting path is textually inside the owner root while pointing anywhere on the host. The file
    // service runs on the DOCKER HOST, so following one would read or overwrite real host files.
    // Creating a symlink needs privilege on Windows (Developer Mode / admin); where it is unavailable the
    // test skips rather than reporting a false pass.
    [SymlinkFact]
    public void Resolve_PathThroughDirectorySymlinkEscapingRoot_Throws()
    {
        var ownerRoot = _resolver.OwnerRoot(_ownerId);
        Directory.CreateDirectory(ownerRoot);

        var outside = Path.Combine(_root, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "top secret");

        Directory.CreateSymbolicLink(Path.Combine(ownerRoot, "esc"), outside);

        var act = () => _resolver.Resolve(_ownerId, "esc/secret.txt");

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [SymlinkFact]
    public void Resolve_WriteTargetInsideEscapingSymlinkDirectory_Throws()
    {
        // The write_file case: the file itself does not exist yet, so only the LINKED PARENT can be
        // resolved. This is the variant that would let a sandbox overwrite an arbitrary host file.
        var ownerRoot = _resolver.OwnerRoot(_ownerId);
        Directory.CreateDirectory(ownerRoot);

        var outside = Path.Combine(_root, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);

        Directory.CreateSymbolicLink(Path.Combine(ownerRoot, "esc"), outside);

        var act = () => _resolver.Resolve(_ownerId, "esc/newly-created.txt");

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [SymlinkFact]
    public void Resolve_SymlinkPointingInsideTheOwnerRoot_IsAllowed()
    {
        // A link that stays inside the sandbox is legitimate and must keep working — the guard rejects
        // escapes, not symlinks as such.
        var ownerRoot = _resolver.OwnerRoot(_ownerId);
        var real = Path.Combine(ownerRoot, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "data.txt"), "fine");

        Directory.CreateSymbolicLink(Path.Combine(ownerRoot, "alias"), real);

        var resolved = _resolver.Resolve(_ownerId, "alias/data.txt");

        File.ReadAllText(resolved).Should().Be("fine");
    }

    [Fact]
    public void Resolve_RelativePathInsideOwnerRoot_ReturnsFullPathUnderIt()
    {
        var result = _resolver.Resolve(_ownerId, "subdir/file.txt");

        result.Should().StartWith(_resolver.OwnerRoot(_ownerId));
    }

    [Fact]
    public void Resolve_NestedLegitimatePath_IsAllowed()
    {
        var act = () => _resolver.Resolve(_ownerId, "a/b/c/d.txt");

        act.Should().NotThrow();
    }

    [Fact]
    public void Resolve_SingleParentTraversal_Throws()
    {
        var act = () => _resolver.Resolve(_ownerId, "../outside.txt");

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Resolve_DoubleParentTraversal_Throws()
    {
        var act = () => _resolver.Resolve(_ownerId, "../../etc/passwd");

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Resolve_ParentTraversalBuriedInsideNestedPath_Throws()
    {
        var act = () => _resolver.Resolve(_ownerId, "a/b/../../../escape.txt");

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Resolve_AbsolutePathOutsideRoot_Throws()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), "some-other-dir", "file.txt");

        var act = () => _resolver.Resolve(_ownerId, outsidePath);

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Resolve_RootedUnixStylePath_Throws()
    {
        // Path.IsPathRooted treats a leading separator as rooted on every platform .NET runs on, even
        // without a drive letter -- this is the "/etc/passwd" shape the model might try.
        var act = () => _resolver.Resolve(_ownerId, "/etc/passwd");

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Resolve_EmptyPath_Throws()
    {
        var act = () => _resolver.Resolve(_ownerId, "");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolve_WhitespacePath_Throws()
    {
        var act = () => _resolver.Resolve(_ownerId, "   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolve_TrailingDotSegment_NormalizesInsideRootWithoutEscaping()
    {
        // GetFullPath normalizes away trailing dots/spaces on the platforms that special-case them
        // (Windows); the important guarantee is that the canonicalized result the caller performs I/O
        // against is checked, not the raw string, so there is no canonicalize-then-use mismatch either way.
        var act = () => _resolver.Resolve(_ownerId, "notes.");

        act.Should().NotThrow();
    }

    [Fact]
    public void Resolve_DifferentOwners_GetDistinctRoots()
    {
        var otherOwner = Guid.NewGuid();

        _resolver.OwnerRoot(_ownerId).Should().NotBe(_resolver.OwnerRoot(otherOwner));
    }

    [Fact]
    public void ToSandboxRelative_RoundTripsResolvedPath()
    {
        var full = _resolver.Resolve(_ownerId, "a/b.txt");

        var relative = _resolver.ToSandboxRelative(_ownerId, full);

        relative.Should().Be("a/b.txt");
    }
}
