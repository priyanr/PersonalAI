using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.TranscriptJson).IsRequired().HasDefaultValue("[]");

        builder.HasIndex(r => r.AgentId);
        builder.HasIndex(r => r.StartedAt);
    }
}
