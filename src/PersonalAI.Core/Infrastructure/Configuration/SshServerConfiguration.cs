using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class SshServerGroupConfiguration : IEntityTypeConfiguration<SshServerGroup>
{
    public void Configure(EntityTypeBuilder<SshServerGroup> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).IsRequired().HasMaxLength(200);
        builder.HasIndex(g => g.Name).IsUnique();
        builder.HasMany(g => g.Servers).WithOne(t => t.Group).HasForeignKey(t => t.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class SshServerConfiguration : IEntityTypeConfiguration<SshServer>
{
    public void Configure(EntityTypeBuilder<SshServer> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Address).IsRequired().HasMaxLength(255);
        builder.Property(t => t.Username).IsRequired().HasMaxLength(100);
        builder.Property(t => t.SecretRef).IsRequired().HasMaxLength(200);
        builder.Property(t => t.HostKeyFingerprint).HasMaxLength(200);
        builder.HasIndex(t => new { t.GroupId, t.Name }).IsUnique();
    }
}
