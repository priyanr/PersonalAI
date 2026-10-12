using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class ModelConfigConfiguration : IEntityTypeConfiguration<ModelConfig>
{
    public void Configure(EntityTypeBuilder<ModelConfig> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ProviderKey).IsRequired().HasMaxLength(50);
        builder.Property(c => c.ModelName).IsRequired().HasMaxLength(300);
        builder.Property(c => c.FieldName).IsRequired().HasMaxLength(100);
        builder.HasIndex(c => new { c.ProviderKey, c.ModelName, c.FieldName }).IsUnique();
    }
}
