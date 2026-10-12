using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class ToolConfigConfiguration : IEntityTypeConfiguration<ToolConfig>
{
    public void Configure(EntityTypeBuilder<ToolConfig> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ToolName).IsRequired().HasMaxLength(50);
        builder.Property(c => c.FieldName).IsRequired().HasMaxLength(100);
        builder.HasIndex(c => new { c.ToolName, c.FieldName }).IsUnique();
    }
}
