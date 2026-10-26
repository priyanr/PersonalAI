using PersonalAI.Core.Services.Agents.Types;

namespace PersonalAI.Tests.Services;

internal static class TestAgentTypes
{
    public static AgentTypeRegistry Registry { get; } =
        new([new GeneralAgentTypeDescriptor(), new DashboardAlertsAgentTypeDescriptor()]);
}
