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
    public DbSet<SshServerGroup> SshServerGroups => Set<SshServerGroup>();
    public DbSet<SshServer> SshServers => Set<SshServer>();
    public DbSet<Dataset> Datasets => Set<Dataset>();
    public DbSet<DataPoint> DataPoints => Set<DataPoint>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();
    public DbSet<DashboardVersion> DashboardVersions => Set<DashboardVersion>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<CollectionJob> CollectionJobs => Set<CollectionJob>();

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
        modelBuilder.ApplyConfiguration(new SshServerGroupConfiguration());
        modelBuilder.ApplyConfiguration(new SshServerConfiguration());
        modelBuilder.ApplyConfiguration(new DatasetConfiguration());
        modelBuilder.ApplyConfiguration(new DataPointConfiguration());
        modelBuilder.ApplyConfiguration(new DashboardConfiguration());
        modelBuilder.ApplyConfiguration(new DashboardVersionConfiguration());
        modelBuilder.ApplyConfiguration(new AlertRuleConfiguration());
        modelBuilder.ApplyConfiguration(new AlertEventConfiguration());
        modelBuilder.ApplyConfiguration(new CollectionJobConfiguration());
    }
}
