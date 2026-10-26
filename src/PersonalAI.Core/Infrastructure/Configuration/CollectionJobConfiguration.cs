using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class CollectionJobConfiguration : IEntityTypeConfiguration<CollectionJob>
{
    public void Configure(EntityTypeBuilder<CollectionJob> builder)
    {
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Name).IsRequired().HasMaxLength(100);
        builder.Property(j => j.Description).HasMaxLength(500);
        builder.Property(j => j.Tool).IsRequired().HasMaxLength(30);
        builder.Property(j => j.Request).IsRequired().HasMaxLength(8000);
        builder.Property(j => j.ParserJson).IsRequired();
        builder.Property(j => j.DatasetJson).IsRequired();
        builder.Property(j => j.Status).IsRequired().HasMaxLength(30);
        builder.Property(j => j.Source).IsRequired().HasMaxLength(10);
        builder.Property(j => j.LastError).HasMaxLength(1000);
        builder.HasIndex(j => new { j.AgentId, j.Name }).IsUnique();
        builder.HasOne<Agent>().WithMany().HasForeignKey(j => j.AgentId).OnDelete(DeleteBehavior.Cascade);
    }
}
