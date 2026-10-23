using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
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

    private static async Task<(SshServersRepository Repo, SshServer Server)> SeedServerAsync(AppDbContext db)
    {
        var repo = new SshServersRepository(db);
        var group = await repo.CreateGroupAsync("prod", null, CancellationToken.None);
        var server = await repo.CreateServerAsync(group.Id, "web-1", "10.0.0.5", 22, "root", "web-key", true, CancellationToken.None);
        return (repo, server!);
    }

    [Fact]
    public async Task CreateGroup_DuplicateName_Throws()
    {
        await using var db = NewDb();
        var repo = new SshServersRepository(db);
        await repo.CreateGroupAsync("prod", null, CancellationToken.None);

        var act = () => repo.CreateGroupAsync("prod", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateServer_UnknownGroup_ReturnsNull()
    {
        await using var db = NewDb();

        var server = await new SshServersRepository(db).CreateServerAsync(Guid.NewGuid(), "x", "h", 22, "u", "s", true, CancellationToken.None);

        server.Should().BeNull();
    }

    [Fact]
    public async Task DeleteGroup_RemovesItsServers()
    {
        await using var db = NewDb();
        var (repo, server) = await SeedServerAsync(db);

        await repo.DeleteGroupAsync(server.GroupId, CancellationToken.None);

        (await repo.GetServersAsync(null, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task TestAsync_FirstSuccess_PinsHostKey()
    {
        await using var db = NewDb();
        var (repo, server) = await SeedServerAsync(db);
        var service = new SshConnectionService(repo, new FakeSsh(new(true, 0, "Linux 6.1\n", "", null, "SHA256:abc")));

        var result = await service.TestAsync(server.Id, CancellationToken.None);

        result!.Success.Should().BeTrue();
        result.ServerInfo.Should().Be("Linux 6.1");
        var saved = await repo.GetServerAsync(server.Id, CancellationToken.None);
        saved!.HostKeyFingerprint.Should().Be("SHA256:abc");
        saved.LastSeenAt.Should().NotBeNull();
        saved.LastError.Should().BeNull();
    }

    [Fact]
    public async Task TestAsync_Failure_DoesNotPinAndRecordsError()
    {
        await using var db = NewDb();
        var (repo, server) = await SeedServerAsync(db);
        var service = new SshConnectionService(repo, new FakeSsh(new(false, -1, "", "", "Permission denied", "SHA256:abc")));

        var result = await service.TestAsync(server.Id, CancellationToken.None);

        result!.Success.Should().BeFalse();
        var saved = await repo.GetServerAsync(server.Id, CancellationToken.None);
        saved!.HostKeyFingerprint.Should().BeNull();
        saved.LastError.Should().Be("Permission denied");
    }

    [Fact]
    public async Task UpdateServer_ChangedAddress_ClearsPinnedHostKey()
    {
        await using var db = NewDb();
        var (repo, server) = await SeedServerAsync(db);
        await repo.RecordConnectionAsync(server.Id, true, null, "SHA256:abc", CancellationToken.None);

        var updated = await repo.UpdateServerAsync(server.Id, server.GroupId, "web-1", "10.0.0.6", 22, "root", "web-key", true, CancellationToken.None);

        updated!.HostKeyFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task UpdateServer_OnlyRenamed_KeepsPinnedHostKey()
    {
        await using var db = NewDb();
        var (repo, server) = await SeedServerAsync(db);
        await repo.RecordConnectionAsync(server.Id, true, null, "SHA256:abc", CancellationToken.None);

        var updated = await repo.UpdateServerAsync(server.Id, server.GroupId, "web-one", "10.0.0.5", 22, "root", "web-key", true, CancellationToken.None);

        updated!.HostKeyFingerprint.Should().Be("SHA256:abc");
    }

    [Fact]
    public async Task Controller_CreateServer_MissingSecret_ReturnsBadRequest()
    {
        await using var db = NewDb();
        var (repo, server) = await SeedServerAsync(db);
        var controller = new SshServersController(repo, new SecretsRepository(db), new SshConnectionService(repo, new FakeSsh(new(true, 0, "", "", null, null))));

        var response = await controller.CreateServer(new SaveSshServerRequest(server.GroupId, "db-1", "10.0.0.9", null, "root", "nope"), CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Controller_CreateServer_Valid_DefaultsPortTo22()
    {
        await using var db = NewDb();
        var (repo, seeded) = await SeedServerAsync(db);
        await new SecretsRepository(db).CreateAsync("SSH_DB_KEY", "pw", CancellationToken.None);
        var controller = new SshServersController(repo, new SecretsRepository(db), new SshConnectionService(repo, new FakeSsh(new(true, 0, "", "", null, null))));

        var response = await controller.CreateServer(new SaveSshServerRequest(seeded.GroupId, "db-1", "10.0.0.9", null, "root", "SSH_DB_KEY"), CancellationToken.None);

        var dto = (response.Result as OkObjectResult)!.Value as SshServerDto;
        dto!.Port.Should().Be(22);
    }
}
