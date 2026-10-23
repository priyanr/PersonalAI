using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).IsRequired().HasMaxLength(200);
        builder.Property(a => a.ProviderKey).IsRequired().HasMaxLength(50).HasDefaultValue("ollama");
        builder.Property(a => a.ModelName).IsRequired().HasMaxLength(200);
        builder.Property(a => a.EnabledSkillIdsJson).IsRequired().HasDefaultValue("[]");
        builder.Property(a => a.EnabledToolsJson).IsRequired().HasDefaultValue("[]");
        builder.Property(a => a.ScheduleKind).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(30).HasDefaultValue(AgentType.General);
        builder.Property(a => a.TypeConfigJson).IsRequired().HasDefaultValue("{}");
        builder.Property(a => a.CronExpression).HasMaxLength(100);

        builder.HasIndex(a => a.CreatedAt);
        builder.HasIndex(a => a.NextRunAt);
    }
}
