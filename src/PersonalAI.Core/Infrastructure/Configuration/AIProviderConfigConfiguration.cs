using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class AIProviderConfigConfiguration : IEntityTypeConfiguration<AIProviderConfig>
{
    public void Configure(EntityTypeBuilder<AIProviderConfig> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ProviderKey).IsRequired().HasMaxLength(50);
        builder.Property(c => c.FieldName).IsRequired().HasMaxLength(100);
        builder.HasIndex(c => new { c.ProviderKey, c.FieldName }).IsUnique();
    }
}
