using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class SandboxInstanceConfiguration : IEntityTypeConfiguration<SandboxInstance>
{
    public void Configure(EntityTypeBuilder<SandboxInstance> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Image).IsRequired().HasMaxLength(200);
        builder.Property(s => s.ContainerId).HasMaxLength(200);
        builder.Property(s => s.HostPath).HasMaxLength(500);
        builder.Property(s => s.PortsJson).IsRequired().HasDefaultValue("[]");
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(s => s.OwnerId);
        builder.HasIndex(s => s.Status);
        builder.HasIndex(s => s.LastUsedAt);
    }
}
