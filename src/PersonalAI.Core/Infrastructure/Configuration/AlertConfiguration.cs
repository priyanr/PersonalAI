using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalAI.Core.Infrastructure.Configuration;

public class AlertRuleConfiguration : IEntityTypeConfiguration<AlertRule>
{
    public void Configure(EntityTypeBuilder<AlertRule> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Description).HasMaxLength(500);
        builder.Property(r => r.Dataset).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Field).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Operator).IsRequired().HasMaxLength(10);
        builder.Property(r => r.Severity).IsRequired().HasMaxLength(20);
        builder.Property(r => r.Source).IsRequired().HasMaxLength(10);
        builder.Property(r => r.Target).HasMaxLength(200);
        builder.HasIndex(r => new { r.AgentId, r.Name }).IsUnique();
        builder.HasOne<Agent>().WithMany().HasForeignKey(r => r.AgentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AlertEventConfiguration : IEntityTypeConfiguration<AlertEvent>
{
    public void Configure(EntityTypeBuilder<AlertEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EntityKey).IsRequired().HasMaxLength(300);
        builder.Property(e => e.EntityLabel).IsRequired().HasMaxLength(300);
        builder.Property(e => e.Message).IsRequired().HasMaxLength(500);
        builder.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.ActionStatus).HasMaxLength(200);
        // Stored as UTC; EF hands back Unspecified, which would otherwise serialize without a 'Z'.
        builder.Property(e => e.FirstSeen).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        builder.Property(e => e.LastSeen).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        builder.Property(e => e.FiredAt).HasConversion(v => v, v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        builder.Property(e => e.ResolvedAt).HasConversion(v => v, v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        builder.Property(e => e.AcknowledgedAt).HasConversion(v => v, v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        builder.HasIndex(e => new { e.RuleId, e.EntityKey, e.State });
        builder.HasIndex(e => new { e.AgentId, e.State });
        builder.HasOne<AlertRule>().WithMany().HasForeignKey(e => e.RuleId).OnDelete(DeleteBehavior.Cascade);
    }
}
