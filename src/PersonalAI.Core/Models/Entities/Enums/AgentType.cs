namespace PersonalAI.Core.Models.Entities.Enums;

public enum AgentType
{
    /// <summary>The original prompt-driven agent: runs its prompt through the agentic loop on its schedule.</summary>
    General = 0,
    /// <summary>Collects data from its data sources on a short deterministic interval (no LLM) and uses the LLM on
    /// its own schedule to design the dashboard and alert rules. See docs/DASHBOARD_AGENTS.md.</summary>
    DashboardAlerts = 1
}
