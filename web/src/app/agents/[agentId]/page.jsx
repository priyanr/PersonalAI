"use client";

import { useState } from "react";
import { useParams, useRouter } from "next/navigation";
import Link from "next/link";
import { useDispatch } from "react-redux";
import { useAgents } from "@/hooks/useAgents";
import { deleteAgent, fetchAgents, updateAgent } from "@/store/agentsSlice";
import { formatNextRun } from "@/lib/schedule";
import AgentForm from "@/components/agents/AgentForm";
import AgentRunPanel from "@/components/agents/AgentRunPanel";
import AgentRunHistory from "@/components/agents/AgentRunHistory";

export default function AgentDetailPage() {
  const { agentId } = useParams();
  const dispatch = useDispatch();
  const router = useRouter();
  const { agents, status } = useAgents();
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);
  const [historyRefreshKey, setHistoryRefreshKey] = useState(0);

  const agent = agents.find((a) => a.id === agentId);
  const nextRun = agent ? formatNextRun(agent) : null;

  async function handleSubmit(values) {
    setSubmitting(true);
    setError(null);
    try {
      await dispatch(updateAgent({ id: agentId, ...values })).unwrap();
    } catch (err) {
      setError(err.message);
    } finally {
      setSubmitting(false);
    }
  }

  async function handleDelete() {
    if (!window.confirm(`Delete agent "${agent.name}"? This cannot be undone.`)) return;
    await dispatch(deleteAgent({ id: agentId })).unwrap();
    router.push("/agents");
  }

  if (status === "loading" && !agent) return <main>Loading agent...</main>;
  if (!agent) return <main className="error-text">Agent not found.</main>;

  return (
    <main>
      <div className="top-nav">
        <h1>{agent.name}</h1>
        <div className="top-nav-links">
          <button type="button" className="secondary" onClick={handleDelete}>
            Delete
          </button>
          <Link href="/agents">← Agents</Link>
        </div>
      </div>

      <p className="muted">
        {nextRun ? `Next run: ${nextRun}` : "Not scheduled"}
        {agent.lastRunAt && ` · Last run: ${new Date(agent.lastRunAt).toLocaleString()}`}
      </p>

      <AgentForm
        key={agent.id}
        initial={agent}
        onSubmit={handleSubmit}
        submitLabel="Save changes"
        submitting={submitting}
        error={error}
      />

      <AgentRunPanel agentId={agent.id} onRunComplete={() => {
          setHistoryRefreshKey((k) => k + 1);
          dispatch(fetchAgents()); // refresh next/last run times
        }}
      />
      <AgentRunHistory agentId={agent.id} refreshKey={historyRefreshKey} />
    </main>
  );
}
