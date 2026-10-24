using PersonalAI.Api;
using PersonalAI.Api.Auth;
using PersonalAI.Api.BackgroundServices;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.Agents;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Services.Ssh;
using PersonalAI.Core.Services.Monitoring;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Services.Monitoring.Dashboards;
using PersonalAI.Core.Services.Monitoring.Alerts;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.AI.Ollama;
using PersonalAI.Core.Services.AI.OpenAiCompatible;
using PersonalAI.Core.Services.Auth;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Core.Services.Search;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Services.Sessions;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Services.Settings;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Core.Services.Tools.Gmail;
using PersonalAI.Core.Tools;
using PersonalAI.Core.Tools.ComputerControl.Services;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Add services to the container.

var authEnabled = builder.Configuration.GetValue<bool>($"{AuthSettings.SectionName}:Enabled");

builder.Services.AddControllers(options =>
{
    if (authEnabled)
        options.Filters.Add(new AuthorizeFilter());
});
builder.Services.AddOpenApi();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.Configure<OllamaSettings>(builder.Configuration.GetSection(OllamaSettings.SectionName));
builder.Services.Configure<SearchSettings>(builder.Configuration.GetSection(SearchSettings.SectionName));
builder.Services.Configure<WorkspaceSettings>(builder.Configuration.GetSection(WorkspaceSettings.SectionName));
builder.Services.Configure<ToolOutputSettings>(builder.Configuration.GetSection(ToolOutputSettings.SectionName));
builder.Services.Configure<AgentSettings>(builder.Configuration.GetSection(AgentSettings.SectionName));
builder.Services.Configure<NetworkScanSettings>(builder.Configuration.GetSection(NetworkScanSettings.SectionName));
builder.Services.Configure<HttpToolSettings>(builder.Configuration.GetSection(HttpToolSettings.SectionName));
builder.Services.Configure<SchedulerSettings>(builder.Configuration.GetSection(SchedulerSettings.SectionName));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection(AuthSettings.SectionName));
builder.Services.Configure<GmailSettings>(builder.Configuration.GetSection(GmailSettings.SectionName));
builder.Services.Configure<SandboxSettings>(builder.Configuration.GetSection(SandboxSettings.SectionName));

// HybridCache: in-process L1 always; Redis L2 only when Redis:Configuration is set (k8s).
var redisConfiguration = builder.Configuration["Redis:Configuration"];
if (!string.IsNullOrWhiteSpace(redisConfiguration))
{
    builder.Services.AddStackExchangeRedisCache(o =>
    {
        o.Configuration = redisConfiguration;
        o.InstanceName = "personalai:";
    });
}
builder.Services.AddHybridCache();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHttpClient<OllamaProvider>((sp, client) =>
{
    var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaSettings>>().Value;
    client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
});
builder.Services.AddHttpClient<OpenAiProvider>();
builder.Services.AddHttpClient<DeepSeekProvider>();
builder.Services.AddHttpClient<AzureAiProvider>();
builder.Services.AddHttpClient<OpenRouterProvider>();
// AddHttpClient<T> above only wires its configured HttpClient (e.g. Ollama's RequestTimeoutSeconds)
// into whatever resolves T by its own concrete type. Registering IAIProvider -> T directly here would
// be a SEPARATE, unrelated DI registration: plain constructor-injection DI, which — finding no
// explicitly registered HttpClient service — falls back to HttpClient's public parameterless
// constructor, i.e. a raw, unconfigured client whose Timeout silently defaults to 100 seconds. Resolve
// the already-correctly-constructed typed-client instance instead so IAIProvider actually gets it.
builder.Services.AddScoped<IAIProvider>(sp => sp.GetRequiredService<OllamaProvider>());
builder.Services.AddScoped<IAIProvider>(sp => sp.GetRequiredService<OpenAiProvider>());
builder.Services.AddScoped<IAIProvider>(sp => sp.GetRequiredService<DeepSeekProvider>());
builder.Services.AddScoped<IAIProvider>(sp => sp.GetRequiredService<AzureAiProvider>());
builder.Services.AddScoped<IAIProvider>(sp => sp.GetRequiredService<OpenRouterProvider>());
builder.Services.AddScoped<AIProviderRegistry>();
builder.Services.AddSingleton<AIProviderConfigCache>();
builder.Services.AddScoped<IAIProviderConfigRepository, AIProviderConfigRepository>();
builder.Services.AddScoped<IModelConfigRepository, ModelConfigRepository>();
builder.Services.AddScoped<AIProviderConfigService>();

builder.Services.AddHttpClient<ISearchProvider, SearXngSearchProvider>();
builder.Services.AddHttpClient(HttpTool.HttpClientName);
builder.Services.AddHttpClient(SandboxHostPowerSignaler.HttpClientName);
builder.Services.AddHttpClient(GmailTool.HttpClientName);
builder.Services.AddHttpClient<GmailOAuthClient>();
builder.Services.AddSingleton<GmailOAuthStateStore>();
builder.Services.AddSingleton<ToolConfigCache>();
builder.Services.AddScoped<IToolConfigRepository, ToolConfigRepository>();
builder.Services.AddScoped<ToolConfigService>();

builder.Services.AddSingleton<Workspace>();
builder.Services.AddSingleton<AppSettingsCache>();
// PersonalAI.Api itself is not marked Windows-only (it targets plain net10.0 and can host the rest of
// the app on any OS), but these four implementations are — ComputerControlTool guards every call with
// OperatingSystem.IsWindows() before touching them, so registering them unconditionally here is safe.
#pragma warning disable CA1416
builder.Services.AddSingleton<IMouseInputService, MouseInputService>();
builder.Services.AddSingleton<IKeyboardInputService, KeyboardInputService>();
builder.Services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
builder.Services.AddSingleton<IUiaElementScanner, UiaElementScanner>();
#pragma warning restore CA1416
builder.Services.AddSingleton(sp => ToolRegistry.CreateDefault(
    sp.GetRequiredService<Workspace>(),
    sp.GetRequiredService<ISearchProvider>(),
    sp.GetRequiredService<AppSettingsCache>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NetworkScanSettings>>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HttpToolSettings>>(),
    sp.GetRequiredService<GmailOAuthClient>(),
    sp.GetRequiredService<ToolConfigCache>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IMouseInputService>(),
    sp.GetRequiredService<IKeyboardInputService>(),
    sp.GetRequiredService<IScreenCaptureService>(),
    sp.GetRequiredService<IUiaElementScanner>(),
    sp.GetRequiredService<ISandboxHostClient>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SandboxSettings>>(),
    sp.GetRequiredService<ISandboxHostPowerSignaler>(),
    sp.GetRequiredService<TimeProvider>(),
    [
        new SshTool(sp.GetRequiredService<CollectionRunner>()),
        new CollectorTestTool(sp.GetRequiredService<CollectionRunner>()),
        new CollectorSaveTool(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<CollectionRunner>()),
        new CollectorListTool(sp.GetRequiredService<IServiceScopeFactory>()),
        new CollectorDeleteTool(sp.GetRequiredService<IServiceScopeFactory>()),
        new DataQueryTool(sp.GetRequiredService<IServiceScopeFactory>()),
        new DashboardGetTool(sp.GetRequiredService<IServiceScopeFactory>()),
        new DashboardSaveTool(sp.GetRequiredService<IServiceScopeFactory>()),
        new AlertRuleSaveTool(sp.GetRequiredService<IServiceScopeFactory>()),
        new AlertRuleDeleteTool(sp.GetRequiredService<IServiceScopeFactory>()),
        new AlertListTool(sp.GetRequiredService<IServiceScopeFactory>())
    ]));

builder.Services.AddScoped<IChatSessionRepository, ChatSessionRepository>();
builder.Services.AddScoped<ChatSessionsService>();
builder.Services.AddScoped<ChatHistoryCompaction>();
builder.Services.AddScoped<ChatAgentService>();
// Singleton: owns every running chat turn, independent of the request that started it.
builder.Services.AddSingleton<ChatTurnRunner>();
builder.Services.AddHostedService<ChatTurnShutdownService>();
builder.Services.AddScoped<IAppSettingsRepository, AppSettingsRepository>();
builder.Services.AddScoped<AppSettingsService>();
builder.Services.AddScoped<SecretsService>();
builder.Services.AddScoped<ISecretsRepository, SecretsRepository>();
builder.Services.AddScoped<ISkillsRepository, SkillsRepository>();
builder.Services.AddScoped<SkillsService>();
builder.Services.AddScoped<SkillExecutionService>();
builder.Services.AddSingleton<IAgentTypeDescriptor, GeneralAgentTypeDescriptor>();
builder.Services.AddSingleton<IAgentTypeDescriptor, DashboardAlertsAgentTypeDescriptor>();
builder.Services.AddSingleton<AgentTypeRegistry>();
builder.Services.AddScoped<ISshServersRepository, SshServersRepository>();
builder.Services.AddScoped<IDatasetRepository, DatasetRepository>();
builder.Services.AddScoped<DatasetService>();
builder.Services.AddSingleton<SshGuardProvider>();
// Data sources: tools that read data from connections they own. Add one here (and its tool) to make a new kind of
// connection available to Dashboard & Alerts agents; the runner and collector tools need no change.
builder.Services.AddScoped<IDataSource, SshDataSource>();
builder.Services.AddScoped<DataSourceRegistry>();
builder.Services.AddSingleton<CollectionRunner>();
builder.Services.AddScoped<ICollectionJobsRepository, CollectionJobsRepository>();
builder.Services.AddScoped<CollectionJobService>();
builder.Services.AddScoped<IDashboardRepository, DashboardRepository>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<IAlertsRepository, AlertsRepository>();
builder.Services.AddScoped<AlertsService>();
builder.Services.AddScoped<AlertRuleService>();
builder.Services.AddScoped<AlertEvaluator>();
builder.Services.AddScoped<ISshRunner, SshNetRunner>();
builder.Services.AddScoped<SshServersService>();
builder.Services.AddScoped<IAgentsRepository, AgentsRepository>();
builder.Services.AddScoped<IAgentRunsRepository, AgentRunsRepository>();
builder.Services.AddScoped<AgentsService>();
builder.Services.AddScoped<AgentExecutionService>();
builder.Services.AddHostedService<AgentSchedulerHostedService>();
builder.Services.AddHostedService<CollectionHostedService>();
builder.Services.AddHostedService<SandboxReaperHostedService>();
builder.Services.AddScoped<ISandboxRepository, SandboxRepository>();
builder.Services.AddScoped<SandboxProvisioningService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SandboxHostRegistry>();
builder.Services.AddSingleton<ISandboxHostClient>(sp => sp.GetRequiredService<SandboxHostRegistry>());
// Singleton is load-bearing, not incidental: its once-per-cooldown/once-per-idle-period debounce state
// must survive across every SandboxTool call and every reaper tick, not reset per DI scope.
builder.Services.AddSingleton<ISandboxHostPowerSignaler, SandboxHostPowerSignaler>();

builder.Services.AddOAuthClient();

if (authEnabled)
{
    var authSettings = builder.Configuration.GetSection(AuthSettings.SectionName).Get<AuthSettings>()!;
    builder.Services.AddDelegatedJwtAuthentication(authSettings);
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy
        .SetIsOriginAllowed(_ => true)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // Content-Disposition isn't in the CORS-safelisted response header set, so without this the
        // sandbox file browser's download/zip filename (lib/api.js's apiGetBlob/apiPostBlob) is
        // unreadable from JS on any cross-origin deployment — including the app's own documented local
        // dev topology (web on :3000, API on :5080).
        .WithExposedHeaders("Content-Disposition"));
});

var app = builder.Build();

app.UseExceptionHandler();

app.UseSerilogRequestLogging();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    var workspaceSettings = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkspaceSettings>>().Value;
    Directory.CreateDirectory(Path.GetFullPath(workspaceSettings.RootPath));

    var appSettings = db.AppSettings.Find(PersonalAI.Core.Models.Entities.AppSettings.SingletonId);
    if (appSettings is not null)
        scope.ServiceProvider.GetRequiredService<AppSettingsCache>().Set(appSettings.WebSearchGuidance, appSettings.ThinkingEnabled);

    // One-time backward-compat seed: existing installs configured Ollama's BaseUrl via appsettings.json
    // rather than the /api/ai-providers UI. Only seeds if no row exists yet, so a user who later edits
    // the URL through the UI is never silently overwritten by the JSON default on a subsequent restart.
    if (!db.AIProviderConfigs.Any(c => c.ProviderKey == OllamaProvider.ProviderKey && c.FieldName == "BaseUrl"))
    {
        var ollamaSettings = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaSettings>>().Value;
        db.AIProviderConfigs.Add(new PersonalAI.Core.Models.Entities.AIProviderConfig
        {
            ProviderKey = OllamaProvider.ProviderKey,
            FieldName = "BaseUrl",
            Value = ollamaSettings.BaseUrl
        });
        db.SaveChanges();
    }

    var providerConfigRepo = scope.ServiceProvider.GetRequiredService<IAIProviderConfigRepository>();
    var providerConfigCache = scope.ServiceProvider.GetRequiredService<AIProviderConfigCache>();
    foreach (var (providerKey, values) in providerConfigRepo.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult())
        providerConfigCache.SetProvider(providerKey, values);
    var modelConfigRepo = scope.ServiceProvider.GetRequiredService<IModelConfigRepository>();
    foreach (var (providerKey, models) in modelConfigRepo.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult())
        foreach (var (modelName, values) in models)
            providerConfigCache.SetModel(providerKey, modelName, values);

    var toolConfigRepo = scope.ServiceProvider.GetRequiredService<IToolConfigRepository>();
    var toolConfigCache = scope.ServiceProvider.GetRequiredService<ToolConfigCache>();
    foreach (var (toolName, values) in toolConfigRepo.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult())
        toolConfigCache.SetTool(toolName, values);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Behind a reverse proxy/ingress: trust X-Forwarded-* so Request.Scheme/Host are the public ones, and
// optionally serve under a sub-path (e.g. PathBase=/PersonalAi). UsePathBase strips the prefix when the
// proxy forwards it and is a no-op when the proxy already stripped it, so either ingress style works.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

var pathBase = builder.Configuration["PathBase"];
if (!string.IsNullOrWhiteSpace(pathBase))
    app.UsePathBase(pathBase);

// Enables the WebSocket upgrade for GET /api/sandbox/host (PersonalAI.SandboxHost's outbound connection).
app.UseWebSockets();

app.UseCors("Frontend");
if (authEnabled)
    app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
