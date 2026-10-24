using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Services;

public class ToolConfigServiceTests
{
    private sealed class ConfigurableTool : ITool
    {
        public string Name => "mail_tool";
        public string Description => "a tool with settings";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";
        public IReadOnlyList<ToolSettingField> SettingsFields =>
        [
            new("ClientId", "Client ID", ToolSettingFieldType.Text, Required: true),
            new("ClientSecret", "Client secret", ToolSettingFieldType.Secret, Required: true),
            new("Connect", "Connect", ToolSettingFieldType.Custom, Required: false, CustomComponentKey: "connect")
        ];

        public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static (ToolConfigService Service, AppDbContext Db, ToolConfigCache Cache) CreateService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var cache = new ToolConfigCache();
        return (new ToolConfigService(db, new ToolConfigRepository(db), new ToolRegistry([new ConfigurableTool()]), cache), db, cache);
    }

    private static UpdateToolConfigRequest Values(params (string Name, string? Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value));

    [Fact]
    public async Task Update_BlankSecret_KeepsTheStoredValueAndNeverReturnsIt()
    {
        var (service, db, cache) = CreateService();
        await using var _ = db;
        await service.Update("mail_tool", Values(("ClientId", "id"), ("ClientSecret", "s3cret")), CancellationToken.None);

        var dto = await service.Update("mail_tool", Values(("ClientId", "id2"), ("ClientSecret", "")), CancellationToken.None);

        cache.GetValue("mail_tool", "ClientSecret").Should().Be("s3cret");
        cache.GetValue("mail_tool", "ClientId").Should().Be("id2");
        dto.IsConfigured.Should().BeTrue();
        dto.Fields.Single(f => f.Name == "ClientSecret").Value.Should().BeNull();
        JsonSerializer.Serialize(await service.GetAll(CancellationToken.None)).Should().NotContain("s3cret");
    }

    [Fact]
    public async Task Update_MissingRequiredField_IsInvalidAndSavesNothing()
    {
        var (service, db, cache) = CreateService();
        await using var _ = db;

        var act = () => service.Update("mail_tool", Values(("ClientId", "id")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>().WithMessage("*ClientSecret*");
        db.ToolConfigs.Should().BeEmpty();
        cache.GetValue("mail_tool", "ClientId").Should().BeNull();
    }

    [Fact]
    public async Task Update_CustomField_IsInvalid()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var act = () => service.Update("mail_tool", Values(("Connect", "x")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task Update_UnknownTool_IsNotFound()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var act = () => service.Update("nope", Values(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task SaveValues_UpsertsOnlyTheGivenFields()
    {
        var (service, db, cache) = CreateService();
        await using var _ = db;
        await service.SaveValues("mail_tool", new Dictionary<string, string?> { ["ClientId"] = "id", ["RefreshToken"] = "r1" }, CancellationToken.None);

        await service.SaveValues("mail_tool", new Dictionary<string, string?> { ["RefreshToken"] = "r2" }, CancellationToken.None);

        db.ToolConfigs.Should().HaveCount(2);
        cache.GetValue("mail_tool", "ClientId").Should().Be("id");
        cache.GetValue("mail_tool", "RefreshToken").Should().Be("r2");
    }
}
