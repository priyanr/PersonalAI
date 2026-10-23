using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class DatasetConfiguration : IEntityTypeConfiguration<Dataset>
{
    public void Configure(EntityTypeBuilder<Dataset> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).IsRequired().HasMaxLength(100);
        builder.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(d => new { d.AgentId, d.Name }).IsUnique();
        builder.HasOne<Agent>().WithMany().HasForeignKey(d => d.AgentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class DataPointConfiguration : IEntityTypeConfiguration<DataPoint>
{
    public void Configure(EntityTypeBuilder<DataPoint> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.EntityKey).IsRequired().HasMaxLength(300);
        builder.Property(p => p.ValuesJson).IsRequired();
        // Stored as UTC; EF hands back Unspecified, which would otherwise serialize without a 'Z'.
        builder.Property(p => p.Ts).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        builder.HasIndex(p => new { p.DatasetId, p.EntityKey, p.Ts });
        builder.HasIndex(p => new { p.DatasetId, p.Ts });
        builder.HasOne<Dataset>().WithMany().HasForeignKey(p => p.DatasetId).OnDelete(DeleteBehavior.Cascade);
    }
}
