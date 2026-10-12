using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class ToolInvocationConfiguration : IEntityTypeConfiguration<ToolInvocation>
{
    public void Configure(EntityTypeBuilder<ToolInvocation> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.ToolName).IsRequired().HasMaxLength(200);
        builder.Property(t => t.ToolCallId).IsRequired().HasMaxLength(100);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);

        builder.HasOne(t => t.RequestingMessage)
            .WithMany()
            .HasForeignKey(t => t.RequestingMessageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.SessionId, t.Status });
    }
}
