using FluentAssertions;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Core.Services.Settings;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Core.Services.Tools.Gmail;
using PersonalAI.Core.Tools;
using PersonalAI.Core.Tools.ComputerControl.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace PersonalAI.Tests.Tools;

public class ToolRegistryTests
{
    private static ToolRegistry CreateRegistry(Workspace workspace, ISearchProvider searchProvider) =>
        ToolRegistry.CreateDefault(
            workspace, searchProvider, new AppSettingsCache(), Options.Create(new NetworkScanSettings()),
            Mock.Of<IHttpClientFactory>(f => f.CreateClient(It.IsAny<string>()) == new HttpClient()),
            Options.Create(new HttpToolSettings()),
            new GmailOAuthClient(new HttpClient(), new ToolConfigCache()),
            new ToolConfigCache(),
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IMouseInputService>(),
            Mock.Of<IKeyboardInputService>(),
            Mock.Of<IScreenCaptureService>(),
            Mock.Of<IUiaElementScanner>(),
            Mock.Of<ISandboxHostClient>(),
            Options.Create(new SandboxSettings()),
            Mock.Of<ISandboxHostPowerSignaler>(),
            TimeProvider.System);

    [Fact]
    public void CreateDefault_RegistersAllExpectedTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "personalai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new Workspace(Options.Create(new WorkspaceSettings { RootPath = root }));
            var searchProvider = Mock.Of<ISearchProvider>();

            var registry = CreateRegistry(workspace, searchProvider);

            var names = registry.Definitions.Select(d => d.Name).ToList();
            names.Should().BeEquivalentTo(["web_search", "read_file", "write_file", "edit_file", "list_files", "search_files", "network_scan", "http_request", "gmail", "computer_control", "sandbox"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryGet_KnownTool_ReturnsTrue()
    {
        var root = Path.Combine(Path.GetTempPath(), "personalai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new Workspace(Options.Create(new WorkspaceSettings { RootPath = root }));
            var registry = CreateRegistry(workspace, Mock.Of<ISearchProvider>());

            registry.TryGet("read_file", out var tool).Should().BeTrue();
            tool.Name.Should().Be("read_file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryGet_UnknownTool_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "personalai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new Workspace(Options.Create(new WorkspaceSettings { RootPath = root }));
            var registry = CreateRegistry(workspace, Mock.Of<ISearchProvider>());

            registry.TryGet("does_not_exist", out _).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
