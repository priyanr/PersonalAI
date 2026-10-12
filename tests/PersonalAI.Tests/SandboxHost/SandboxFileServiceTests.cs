using FluentAssertions;
using Microsoft.Extensions.Options;
using PersonalAI.Sandbox.Protocol;
using PersonalAI.SandboxHost;

namespace PersonalAI.Tests.SandboxHost;

/// <summary>Exercises every FileOp against a real temp directory -- no mocking of the filesystem, since
/// SandboxFileService's whole contract is "plain System.IO against {root}/{ownerId}".</summary>
public class SandboxFileServiceTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly BrokerSettings _settings = new();
    private readonly SandboxFileService _service;

    public SandboxFileServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "personalai-sandboxhost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var resolver = new SandboxPathResolver(_root);
        _service = new SandboxFileService(resolver, Options.Create(_settings));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static FileOpRequest Req(FileOp op, string path,
        string? destinationPath = null, string? contentBase64 = null, string? oldText = null,
        string? newText = null, string? pattern = null, bool append = false, bool recursive = false,
        int offset = 0, int limit = 0, long maxBytes = 0, IReadOnlyList<string>? selectedPaths = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), op, path, destinationPath, contentBase64, oldText, newText,
            pattern, append, recursive, offset, limit, maxBytes, selectedPaths);

    private static string Base64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
    private static string Text(string base64) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    [Fact]
    public async Task Write_ThenRead_RoundTripsContent()
    {
        var write = await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "hello.txt", contentBase64: Base64("hi there")), CancellationToken.None);
        write.Success.Should().BeTrue();

        var read = await _service.ExecuteAsync(_ownerId, Req(FileOp.Read, "hello.txt"), CancellationToken.None);
        read.Success.Should().BeTrue();
        Text(read.ContentBase64!).Should().Be("hi there");
    }

    [Fact]
    public async Task Write_Append_AppendsRatherThanOverwrites()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "log.txt", contentBase64: Base64("a")), CancellationToken.None);
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "log.txt", contentBase64: Base64("b"), append: true), CancellationToken.None);

        var read = await _service.ExecuteAsync(_ownerId, Req(FileOp.Read, "log.txt"), CancellationToken.None);

        Text(read.ContentBase64!).Should().Be("ab");
    }

    [Fact]
    public async Task Write_CreatesParentDirectories()
    {
        var result = await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "nested/dir/file.txt", contentBase64: Base64("x")), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(_root, _ownerId.ToString(), "nested", "dir", "file.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Write_ContentExceedingDefaultCap_FailsUnlessMaxBytesOverridesIt()
    {
        _settings.MaxFileTransferBytes = 4;
        var content = Base64("way bigger than four bytes");

        var withoutOverride = await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "a.txt", contentBase64: content), CancellationToken.None);
        withoutOverride.Success.Should().BeFalse();

        // The sandbox file browser's save-from-editor path passes MaxBytes to match the larger cap it
        // used to view the file in the first place — a file you could open you must also be able to save.
        var withOverride = await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "a.txt", contentBase64: content, maxBytes: 1_000_000), CancellationToken.None);
        withOverride.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Read_NonExistentFile_ReturnsError()
    {
        var result = await _service.ExecuteAsync(_ownerId, Req(FileOp.Read, "missing.txt"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Read_WithOffsetAndLimit_ReturnsLineWindow()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "lines.txt", contentBase64: Base64("l1\nl2\nl3\nl4\nl5")), CancellationToken.None);

        var read = await _service.ExecuteAsync(_ownerId, Req(FileOp.Read, "lines.txt", offset: 1, limit: 2), CancellationToken.None);

        Text(read.ContentBase64!).Should().Be("l2\nl3");
    }

    [Fact]
    public async Task Edit_ReplacesAllOccurrencesAndReportsCount()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "code.py", contentBase64: Base64("foo=1\nfoo=2\n")), CancellationToken.None);

        var edit = await _service.ExecuteAsync(_ownerId, Req(FileOp.Edit, "code.py", oldText: "foo", newText: "bar"), CancellationToken.None);

        edit.Success.Should().BeTrue();
        edit.ReplacementCount.Should().Be(2);

        // Read is line-based (it re-joins File.ReadAllLines), which normalizes away a trailing newline --
        // this assertion checks the replacement took effect, not exact byte-for-byte round-tripping.
        var read = await _service.ExecuteAsync(_ownerId, Req(FileOp.Read, "code.py"), CancellationToken.None);
        Text(read.ContentBase64!).Should().Be("bar=1\nbar=2");
    }

    [Fact]
    public async Task Edit_NoMatch_ReturnsZeroReplacementsAndLeavesFileUnchanged()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "code.py", contentBase64: Base64("unchanged")), CancellationToken.None);

        var edit = await _service.ExecuteAsync(_ownerId, Req(FileOp.Edit, "code.py", oldText: "nope", newText: "x"), CancellationToken.None);

        edit.Success.Should().BeTrue();
        edit.ReplacementCount.Should().Be(0);
    }

    [Fact]
    public async Task List_ReturnsEntriesForDirectory()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "a.txt", contentBase64: Base64("1")), CancellationToken.None);
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "b.txt", contentBase64: Base64("2")), CancellationToken.None);

        var list = await _service.ExecuteAsync(_ownerId, Req(FileOp.List, "."), CancellationToken.None);

        list.Success.Should().BeTrue();
        list.Entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_Recursive_IncludesNestedEntries()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "sub/nested.txt", contentBase64: Base64("x")), CancellationToken.None);

        var list = await _service.ExecuteAsync(_ownerId, Req(FileOp.List, ".", recursive: true), CancellationToken.None);

        list.Entries!.Select(e => e.Path).Should().Contain("sub/nested.txt");
    }

    [Fact]
    public async Task List_NonRecursive_DoesNotDescendIntoSubdirectories()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "sub/nested.txt", contentBase64: Base64("x")), CancellationToken.None);

        var list = await _service.ExecuteAsync(_ownerId, Req(FileOp.List, ".", recursive: false), CancellationToken.None);

        list.Entries!.Select(e => e.Path).Should().NotContain("sub/nested.txt");
    }

    [Fact]
    public async Task List_SkipsIgnoredDirectoriesWhenRecursive()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, ".git/config", contentBase64: Base64("x")), CancellationToken.None);
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "src/main.py", contentBase64: Base64("x")), CancellationToken.None);

        var list = await _service.ExecuteAsync(_ownerId, Req(FileOp.List, ".", recursive: true), CancellationToken.None);

        list.Entries!.Select(e => e.Path).Should().NotContain(e => e.Contains(".git"));
        list.Entries!.Select(e => e.Path).Should().Contain("src/main.py");
    }

    [Fact]
    public async Task List_RespectsMaxListEntriesAndReportsTruncated()
    {
        _settings.MaxListEntries = 2;
        for (var i = 0; i < 5; i++)
            await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, $"f{i}.txt", contentBase64: Base64("x")), CancellationToken.None);

        var list = await _service.ExecuteAsync(_ownerId, Req(FileOp.List, "."), CancellationToken.None);

        list.Entries.Should().HaveCount(2);
        list.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Search_FindsMatchingLinesAcrossFiles()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "a.py", contentBase64: Base64("import os\nprint('hi')\n")), CancellationToken.None);
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "b.py", contentBase64: Base64("import sys\n")), CancellationToken.None);

        var search = await _service.ExecuteAsync(_ownerId, Req(FileOp.Search, ".", pattern: "^import"), CancellationToken.None);

        search.Success.Should().BeTrue();
        search.Hits.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_RespectsMaxSearchHitsAndReportsTruncated()
    {
        _settings.MaxSearchHits = 1;
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "a.txt", contentBase64: Base64("hit\nhit\nhit\n")), CancellationToken.None);

        var search = await _service.ExecuteAsync(_ownerId, Req(FileOp.Search, ".", pattern: "hit"), CancellationToken.None);

        search.Hits.Should().HaveCount(1);
        search.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_File_RemovesIt()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "gone.txt", contentBase64: Base64("x")), CancellationToken.None);

        var delete = await _service.ExecuteAsync(_ownerId, Req(FileOp.Delete, "gone.txt"), CancellationToken.None);

        delete.Success.Should().BeTrue();
        File.Exists(Path.Combine(_root, _ownerId.ToString(), "gone.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NonEmptyDirectoryWithoutRecursive_Fails()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "dir/file.txt", contentBase64: Base64("x")), CancellationToken.None);

        var delete = await _service.ExecuteAsync(_ownerId, Req(FileOp.Delete, "dir", recursive: false), CancellationToken.None);

        delete.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NonEmptyDirectoryRecursive_Succeeds()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "dir/file.txt", contentBase64: Base64("x")), CancellationToken.None);

        var delete = await _service.ExecuteAsync(_ownerId, Req(FileOp.Delete, "dir", recursive: true), CancellationToken.None);

        delete.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, _ownerId.ToString(), "dir")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_File_RelocatesIt()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "old.txt", contentBase64: Base64("payload")), CancellationToken.None);

        var move = await _service.ExecuteAsync(_ownerId, Req(FileOp.Move, "old.txt", destinationPath: "renamed/new.txt"), CancellationToken.None);

        move.Success.Should().BeTrue();
        File.Exists(Path.Combine(_root, _ownerId.ToString(), "old.txt")).Should().BeFalse();
        var read = await _service.ExecuteAsync(_ownerId, Req(FileOp.Read, "renamed/new.txt"), CancellationToken.None);
        Text(read.ContentBase64!).Should().Be("payload");
    }

    [Fact]
    public async Task MakeDirectory_CreatesDirectory()
    {
        var result = await _service.ExecuteAsync(_ownerId, Req(FileOp.MakeDirectory, "brand/new/dir"), CancellationToken.None);

        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, _ownerId.ToString(), "brand", "new", "dir")).Should().BeTrue();
    }

    [Fact]
    public async Task ReadRaw_ReturnsExactBytesForABinaryFixture()
    {
        // Bytes that File.ReadAllLines/UTF-8 text decoding would corrupt (an embedded NUL, a lone
        // continuation byte, no line structure at all) — exactly what ReadRaw exists to read safely,
        // unlike Read.
        byte[] rawBytes = [0x00, 0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x4A, 0x46, 0x49, 0x46];
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "photo.jpg", contentBase64: Convert.ToBase64String(rawBytes)), CancellationToken.None);

        var read = await _service.ExecuteAsync(_ownerId, Req(FileOp.ReadRaw, "photo.jpg"), CancellationToken.None);

        read.Success.Should().BeTrue();
        Convert.FromBase64String(read.ContentBase64!).Should().BeEquivalentTo(rawBytes);
        read.TotalSize.Should().Be(rawBytes.LongLength);
        read.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ReadRaw_FileExceedingCap_FailsOutrightRatherThanTruncating()
    {
        _settings.MaxDownloadBytes = 4;
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "big.bin", contentBase64: Base64("way too big")), CancellationToken.None);

        var read = await _service.ExecuteAsync(_ownerId, Req(FileOp.ReadRaw, "big.bin"), CancellationToken.None);

        read.Success.Should().BeFalse("a truncated binary download would be corrupt, unlike truncated text for model context");
        read.ContentBase64.Should().BeNull();
    }

    [Fact]
    public async Task ReadRaw_NonExistentFile_ReturnsError()
    {
        var read = await _service.ExecuteAsync(_ownerId, Req(FileOp.ReadRaw, "missing.bin"), CancellationToken.None);

        read.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Zip_WholeDirectory_IncludesAllFilesAndRespectsIgnoredDirectories()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "src/main.py", contentBase64: Base64("print(1)")), CancellationToken.None);
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "notes.txt", contentBase64: Base64("hi")), CancellationToken.None);
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, ".git/config", contentBase64: Base64("x")), CancellationToken.None);

        var zip = await _service.ExecuteAsync(_ownerId, Req(FileOp.Zip, "."), CancellationToken.None);

        zip.Success.Should().BeTrue();
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(Convert.FromBase64String(zip.ContentBase64!)));
        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["src/main.py", "notes.txt"]);
    }

    [Fact]
    public async Task Zip_SelectedPaths_IncludesOnlyThoseFilesAndDirectories()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "a.txt", contentBase64: Base64("a")), CancellationToken.None);
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "b.txt", contentBase64: Base64("b")), CancellationToken.None);
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "dir/c.txt", contentBase64: Base64("c")), CancellationToken.None);

        var zip = await _service.ExecuteAsync(_ownerId, Req(FileOp.Zip, ".", selectedPaths: ["a.txt", "dir"]), CancellationToken.None);

        zip.Success.Should().BeTrue();
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(Convert.FromBase64String(zip.ContentBase64!)));
        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["a.txt", "dir/c.txt"]);
    }

    [Fact]
    public async Task Zip_SelectedPathsOverlapping_DoesNotDoubleCountBytesOrWriteDuplicateEntries()
    {
        // A directory plus a file already inside it -- WalkForZip adds "dir/c.txt" while walking "dir",
        // then the explicit "dir/c.txt" entry must be skipped rather than added (and counted) again.
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "dir/c.txt", contentBase64: Base64("c")), CancellationToken.None);

        var zip = await _service.ExecuteAsync(_ownerId, Req(FileOp.Zip, ".", selectedPaths: ["dir", "dir/c.txt"]), CancellationToken.None);

        zip.Success.Should().BeTrue();
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(Convert.FromBase64String(zip.ContentBase64!)));
        archive.Entries.Should().ContainSingle(e => e.FullName == "dir/c.txt");
    }

    [Fact]
    public async Task Zip_SelectionExceedingCap_AbortsBeforeBuildingTheArchive()
    {
        _settings.MaxZipBytes = 4;
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "big.txt", contentBase64: Base64("way too big to fit the cap")), CancellationToken.None);

        var zip = await _service.ExecuteAsync(_ownerId, Req(FileOp.Zip, "."), CancellationToken.None);

        zip.Success.Should().BeFalse();
        zip.ContentBase64.Should().BeNull();
    }

    [Fact]
    public async Task Zip_EmptySelection_ReturnsError()
    {
        var zip = await _service.ExecuteAsync(_ownerId, Req(FileOp.Zip, ".", selectedPaths: []), CancellationToken.None);

        zip.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Zip_SelectedPathThatDoesNotExist_ReturnsError()
    {
        var zip = await _service.ExecuteAsync(_ownerId, Req(FileOp.Zip, ".", selectedPaths: ["nope.txt"]), CancellationToken.None);

        zip.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Zip_SelectedPathEscapingOwnerRoot_FailsWithoutThrowing()
    {
        var zip = await _service.ExecuteAsync(_ownerId, Req(FileOp.Zip, ".", selectedPaths: ["../escape.txt"]), CancellationToken.None);

        zip.Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(FileOp.Read)]
    [InlineData(FileOp.Write)]
    [InlineData(FileOp.Edit)]
    [InlineData(FileOp.List)]
    [InlineData(FileOp.Search)]
    [InlineData(FileOp.Delete)]
    [InlineData(FileOp.MakeDirectory)]
    [InlineData(FileOp.ReadRaw)]
    public async Task EveryOp_PathEscapingOwnerRoot_FailsWithoutThrowing(FileOp op)
    {
        var request = Req(op, "../escape.txt", contentBase64: Base64("x"), oldText: "a", newText: "b", pattern: "a");

        var result = await _service.ExecuteAsync(_ownerId, request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Move_DestinationEscapingOwnerRoot_FailsWithoutThrowing()
    {
        await _service.ExecuteAsync(_ownerId, Req(FileOp.Write, "src.txt", contentBase64: Base64("x")), CancellationToken.None);

        var result = await _service.ExecuteAsync(_ownerId, Req(FileOp.Move, "src.txt", destinationPath: "../escape.txt"), CancellationToken.None);

        result.Success.Should().BeFalse();
    }
}
