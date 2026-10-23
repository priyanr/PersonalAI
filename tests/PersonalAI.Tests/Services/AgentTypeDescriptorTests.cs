using FluentAssertions;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents.Types;

namespace PersonalAI.Tests.Services;

public class AgentTypeDescriptorTests
{
    private readonly DashboardAlertsAgentTypeDescriptor _descriptor = new();

    [Fact]
    public void NormalizeConfig_Blank_ReturnsDefaults()
    {
        var (json, error) = _descriptor.NormalizeConfig(null);

        error.Should().BeNull();
        json.Should().Contain("\"collectIntervalMinutes\":5");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void NormalizeConfig_CollectIntervalOutOfRange_ReturnsError(int minutes)
    {
        var (json, error) = _descriptor.NormalizeConfig($"{{\"collectIntervalMinutes\":{minutes}}}");

        json.Should().BeNull();
        error.Should().Contain("between 1 and 5");
    }

    [Fact]
    public void NormalizeConfig_ValidValues_RoundTrips()
    {
        var (json, error) = _descriptor.NormalizeConfig("{\"collectIntervalMinutes\":2,\"retentionDays\":7}");

        error.Should().BeNull();
        json.Should().Contain("\"collectIntervalMinutes\":2").And.Contain("\"retentionDays\":7");
    }

    [Fact]
    public void Registry_ResolvesBothTypes_AndGeneralAddsNoPreamble()
    {
        TestAgentTypes.Registry.Get(AgentType.General).SystemPromptPreamble.Should().BeNull();
        TestAgentTypes.Registry.Get(AgentType.DashboardAlerts).SystemPromptPreamble.Should().NotBeNullOrWhiteSpace();
    }
}
