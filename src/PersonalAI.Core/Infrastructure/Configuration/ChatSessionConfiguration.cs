using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.ProviderKey).IsRequired().HasMaxLength(50).HasDefaultValue("ollama");
        builder.Property(s => s.ModelName).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Title).HasMaxLength(200);
        builder.Property(s => s.ToolsEnabled).HasDefaultValue(true);
        builder.Property(s => s.TrimHistoryEnabled).HasDefaultValue(true);
        builder.Property(s => s.WebSearchEnabled).HasDefaultValue(true);
        builder.Property(s => s.ReadFileEnabled).HasDefaultValue(true);
        builder.Property(s => s.WriteFileEnabled).HasDefaultValue(true);
        builder.Property(s => s.EditFileEnabled).HasDefaultValue(true);
        builder.Property(s => s.ListFilesEnabled).HasDefaultValue(true);
        builder.Property(s => s.SearchFilesEnabled).HasDefaultValue(true);
        builder.Property(s => s.NetworkScanEnabled).HasDefaultValue(false);
        builder.Property(s => s.HttpRequestEnabled).HasDefaultValue(true);
        builder.Property(s => s.ComputerControlEnabled).HasDefaultValue(false);
        builder.Property(s => s.SandboxEnabled).HasDefaultValue(false);
        builder.Property(s => s.GmailEnabled).HasDefaultValue(false);

        builder.HasMany(s => s.Messages)
            .WithOne(m => m.Session)
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.ToolInvocations)
            .WithOne(t => t.Session)
            .HasForeignKey(t => t.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.CreatedAt);
    }
}
