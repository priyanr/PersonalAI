using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Services.Ssh;

namespace PersonalAI.Tests.Services;

public class SshServersTests
{
    private sealed class FakeSsh(SshCommandResult result) : ISshRunner
    {
        public Task<SshCommandResult> RunAsync(SshServer server, string command, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SshServersService NewService(AppDbContext db, SshCommandResult? probe = null) =>
        new(db, new SshServersRepository(db), new SecretsRepository(db), new FakeSsh(probe ?? new(true, 0, "", "", null, null)));

    private static async Task<SshServer> SeedServerAsync(AppDbContext db)
    {
        db.Secrets.Add(new Secret { Key = "SSH_WEB_KEY", Value = "pw" });
        var group = await TestSeed.SshGroupAsync(db, "prod", null, CancellationToken.None);
        return await TestSeed.SshServerAsync(db, group.Id, "web-1", "10.0.0.5", 22, "root", "SSH_WEB_KEY", true, CancellationToken.None);
    }

    private static SaveSshServerRequest Request(SshServer server, string name, string address) =>
        new(server.GroupId, name, address, server.Port, server.Username, server.SecretRef);

    [Fact]
    public async Task CreateGroup_DuplicateName_Conflicts()
    {
        await using var db = NewDb();
        var service = NewService(db);
        await service.CreateGroup(new SaveSshServerGroupRequest("prod", null), CancellationToken.None);

        var act = () => service.CreateGroup(new SaveSshServerGroupRequest(" prod ", null), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task CreateServer_UnknownGroup_IsInvalid()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);

        var act = () => NewService(db).CreateServer(Request(server, "x", "h") with { GroupId = Guid.NewGuid() }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>().WithMessage("Group not found.");
    }

    [Fact]
    public async Task CreateServer_DuplicateNameInGroup_Conflicts()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);

        var act = () => NewService(db).CreateServer(Request(server, "web-1", "10.0.0.9"), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task DeleteGroup_RemovesItsServers()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);

        await NewService(db).DeleteGroup(server.GroupId, CancellationToken.None);

        (await new SshServersRepository(db).GetServersAsync(null, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnection_FirstSuccess_PinsHostKey()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);
        var service = NewService(db, new(true, 0, "Linux 6.1\n", "", null, "SHA256:abc"));

        var result = await service.TestConnection(server.Id, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ServerInfo.Should().Be("Linux 6.1");
        var saved = await service.GetServer(server.Id, CancellationToken.None);
        saved.HostKeyFingerprint.Should().Be("SHA256:abc");
        saved.LastSeenAt.Should().NotBeNull();
        saved.LastError.Should().BeNull();
    }

    [Fact]
    public async Task TestConnection_Failure_DoesNotPinAndRecordsError()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);
        var service = NewService(db, new(false, -1, "", "", "Permission denied", "SHA256:abc"));

        var result = await service.TestConnection(server.Id, CancellationToken.None);

        result.Success.Should().BeFalse();
        var saved = await service.GetServer(server.Id, CancellationToken.None);
        saved.HostKeyFingerprint.Should().BeNull();
        saved.LastError.Should().Be("Permission denied");
    }

    [Fact]
    public async Task UpdateServer_ChangedAddress_ClearsPinnedHostKey()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);
        server.RecordConnection(true, null, "SHA256:abc");
        await db.SaveChangesAsync();

        var updated = await NewService(db).UpdateServer(server.Id, Request(server, "web-1", "10.0.0.6"), CancellationToken.None);

        updated.HostKeyFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task UpdateServer_OnlyRenamed_KeepsPinnedHostKey()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);
        server.RecordConnection(true, null, "SHA256:abc");
        await db.SaveChangesAsync();

        var updated = await NewService(db).UpdateServer(server.Id, Request(server, "web-one", "10.0.0.5"), CancellationToken.None);

        updated.HostKeyFingerprint.Should().Be("SHA256:abc");
    }

    [Fact]
    public void RecordConnection_NeverReplacesAPinnedHostKey()
    {
        var server = new SshServer { Name = "s", Address = "a", Username = "u", SecretRef = "SSH_K", HostKeyFingerprint = "SHA256:old" };

        server.RecordConnection(true, null, "SHA256:new");

        server.HostKeyFingerprint.Should().Be("SHA256:old");
    }

    [Fact]
    public async Task CreateServer_MissingSecret_IsInvalid()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);

        var act = () => NewService(db).CreateServer(Request(server, "db-1", "10.0.0.9") with { SecretRef = "SSH_NOPE" }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>().WithMessage("No secret named 'SSH_NOPE'*");
    }

    [Fact]
    public async Task CreateServer_Valid_DefaultsPortTo22()
    {
        await using var db = NewDb();
        var server = await SeedServerAsync(db);

        var dto = await NewService(db).CreateServer(Request(server, "db-1", "10.0.0.9") with { Port = null }, CancellationToken.None);

        dto.Port.Should().Be(22);
    }
}
