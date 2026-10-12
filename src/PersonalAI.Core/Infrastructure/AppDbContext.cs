using PersonalAI.Core.Infrastructure.Configuration;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ToolInvocation> ToolInvocations => Set<ToolInvocation>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<AIProviderConfig> AIProviderConfigs => Set<AIProviderConfig>();
    public DbSet<ModelConfig> ModelConfigs => Set<ModelConfig>();
    public DbSet<ToolConfig> ToolConfigs => Set<ToolConfig>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<SandboxInstance> SandboxInstances => Set<SandboxInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ChatSessionConfiguration());
        modelBuilder.ApplyConfiguration(new ChatMessageConfiguration());
        modelBuilder.ApplyConfiguration(new ToolInvocationConfiguration());
        modelBuilder.ApplyConfiguration(new AppSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new AIProviderConfigConfiguration());
        modelBuilder.ApplyConfiguration(new ModelConfigConfiguration());
        modelBuilder.ApplyConfiguration(new ToolConfigConfiguration());
        modelBuilder.ApplyConfiguration(new SecretConfiguration());
        modelBuilder.ApplyConfiguration(new SkillConfiguration());
        modelBuilder.ApplyConfiguration(new AgentConfiguration());
        modelBuilder.ApplyConfiguration(new AgentRunConfiguration());
        modelBuilder.ApplyConfiguration(new SandboxInstanceConfiguration());
    }
}
