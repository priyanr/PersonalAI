"use client";

import { useParams } from "next/navigation";
import Link from "next/link";
import { useAgents } from "@/hooks/useAgents";
import { AgentType } from "@/lib/enums";
import DashboardView from "@/components/dashboard/DashboardView";

export default function AgentDashboardPage() {
  const { agentId } = useParams();
  const { agents, status } = useAgents();
  const agent = agents.find((a) => a.id === agentId);

  if (status === "loading" && !agent) return <main>Loading…</main>;
  if (!agent) return <main className="error-text">Agent not found.</main>;
  if (agent.type !== AgentType.DashboardAlerts)
    return (
      <main>
        <p className="muted">
          &quot;{agent.name}&quot; is not a Dashboard &amp; Alerts agent. <Link href={`/agents/${agent.id}`}>Open agent</Link>
        </p>
      </main>
    );

  return (
    <main>
      <div className="top-nav">
        <h1>{agent.name}</h1>
        <div className="top-nav-links">
          <Link href={`/agents/${agent.id}`}>Settings</Link>
          <Link href="/dashboards">← Dashboards</Link>
        </div>
      </div>
      <DashboardView agentId={agent.id} agentName={agent.name} />
    </main>
  );
}
