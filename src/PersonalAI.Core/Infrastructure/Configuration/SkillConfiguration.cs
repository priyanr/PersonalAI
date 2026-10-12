using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.ProviderKey).IsRequired().HasMaxLength(50).HasDefaultValue("ollama");
        builder.Property(s => s.ModelName).IsRequired().HasMaxLength(200);
        builder.Property(s => s.EnabledToolsJson).IsRequired().HasDefaultValue("[]");
        builder.Property(s => s.SecretKeysJson).IsRequired().HasDefaultValue("[]");

        builder.HasIndex(s => s.CreatedAt);
    }
}
