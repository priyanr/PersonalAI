using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class DashboardConfiguration : IEntityTypeConfiguration<Dashboard>
{
    public void Configure(EntityTypeBuilder<Dashboard> builder)
    {
        builder.HasKey(d => d.Id);
        builder.HasIndex(d => d.AgentId).IsUnique();
        builder.HasOne<Agent>().WithMany().HasForeignKey(d => d.AgentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class DashboardVersionConfiguration : IEntityTypeConfiguration<DashboardVersion>
{
    public void Configure(EntityTypeBuilder<DashboardVersion> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.SpecJson).IsRequired();
        builder.Property(v => v.Reason).HasMaxLength(500);
        builder.HasIndex(v => new { v.DashboardId, v.Version }).IsUnique();
        builder.HasOne<Dashboard>().WithMany().HasForeignKey(v => v.DashboardId).OnDelete(DeleteBehavior.Cascade);
    }
}
