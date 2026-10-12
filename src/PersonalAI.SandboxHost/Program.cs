using PersonalAI.SandboxHost;

// Generic Host worker — no Kestrel, no web server, just one BackgroundService dialing out to the API.
// Host.CreateApplicationBuilder already wires up appsettings.json + appsettings.{Environment}.json +
// environment variables (Section__Key, matching the rest of this repo's convention) + command line args,
// in that precedence order, so no extra configuration wiring is needed here.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<BrokerSettings>(builder.Configuration.GetSection(BrokerSettings.SectionName));

builder.Services.AddSingleton(sp => new SandboxPathResolver(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BrokerSettings>>().Value.SandboxRootPath));
builder.Services.AddSingleton<SandboxFileService>();
builder.Services.AddSingleton<DockerSandboxManager>();
builder.Services.AddHostedService<BrokerConnection>();

var host = builder.Build();
host.Run();
