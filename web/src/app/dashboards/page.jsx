"use client";

import Link from "next/link";
import { useAgents } from "@/hooks/useAgents";
import { AgentType } from "@/lib/enums";

// "ssh, sql" for the data tools the agent is scoped to (or the legacy SSH group list).
function dataSourceSummary(config) {
  const tools = Object.entries(config?.toolScopes ?? {})
    .filter(([, ids]) => ids.length > 0)
    .map(([tool]) => tool);
  if (tools.length === 0 && config?.targetGroupIds?.length) tools.push("ssh");
  return tools.length > 0 ? `Data sources: ${tools.join(", ")}` : "No data sources selected";
}

export default function DashboardsPage() {
  const { agents, status } = useAgents();
  const dashboards = agents.filter((a) => a.type === AgentType.DashboardAlerts);

  return (
    <main>
      <h1>Dashboards</h1>
      <p className="muted">
        Each Dashboard &amp; Alerts agent keeps one live dashboard, designed by the model from the data it collects.
      </p>
      {status === "loading" && <p className="muted">Loading…</p>}
      {status !== "loading" && dashboards.length === 0 && (
        <p className="muted">
          None yet. <Link href="/agents/new">Create an agent</Link> and choose the Dashboard &amp; Alerts type.
        </p>
      )}
      <ul className="skill-list">
        {dashboards.map((agent) => (
          <li key={agent.id} className="skill-list-item-wrap">
            <Link href={`/agents/${agent.id}/dashboard`} className="skill-list-item">
              <span className="skill-name">{agent.name}</span>
              {agent.description && <span className="skill-description">{agent.description}</span>}
              <span className="skill-meta">
                {dataSourceSummary(agent.typeConfig)}
                {!agent.enabled && " · paused"}
              </span>
            </Link>
          </li>
        ))}
      </ul>
    </main>
  );
}
