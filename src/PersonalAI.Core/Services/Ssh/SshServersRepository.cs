using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Ssh;

public interface ISshServersRepository
{
    Task<List<SshServerGroup>> GetGroupsAsync(CancellationToken ct);
    Task<SshServerGroup?> GetGroupAsync(Guid id, CancellationToken ct);
    /// <summary>The group with its servers, for deleting both.</summary>
    Task<SshServerGroup?> GetGroupWithServersAsync(Guid id, CancellationToken ct);

    Task<List<SshServer>> GetServersAsync(Guid? groupId, CancellationToken ct);
    Task<SshServer?> GetServerAsync(Guid id, CancellationToken ct);
}

public class SshServersRepository(AppDbContext db) : ISshServersRepository
{
    public async Task<List<SshServerGroup>> GetGroupsAsync(CancellationToken ct)
    {
        return await db.SshServerGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
    }

    public async Task<SshServerGroup?> GetGroupAsync(Guid id, CancellationToken ct)
    {
        return await db.SshServerGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    public async Task<SshServerGroup?> GetGroupWithServersAsync(Guid id, CancellationToken ct)
    {
        return await db.SshServerGroups.Include(g => g.Servers).FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    public async Task<List<SshServer>> GetServersAsync(Guid? groupId, CancellationToken ct)
    {
        var query = db.SshServers.AsNoTracking();
        if (groupId is not null) query = query.Where(t => t.GroupId == groupId);
        return await query.OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<SshServer?> GetServerAsync(Guid id, CancellationToken ct)
    {
        return await db.SshServers.FirstOrDefaultAsync(t => t.Id == id, ct);
    }
}
