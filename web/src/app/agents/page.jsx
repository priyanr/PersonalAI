"use client";

import Link from "next/link";
import { useAgents } from "@/hooks/useAgents";
import AgentList from "@/components/agents/AgentList";

export default function AgentsPage() {
  const { agents, status, error } = useAgents();

  return (
    <main>
      <div className="top-nav">
        <h1>Agents</h1>
        <div className="top-nav-links">
          <Link href="/agents/new" className="button-link">New agent</Link>
        </div>
      </div>
      <p className="muted">
        An agent has its own prompt and invokes your <Link href="/settings/skills">skills</Link> as tools — run it
        manually here, or arm a schedule so it fires unattended. Every run is recorded below on its page.
      </p>
      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "failed" && <p className="error-text">{error}</p>}
      <AgentList agents={agents} />
    </main>
  );
}
