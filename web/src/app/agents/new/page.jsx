"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useDispatch } from "react-redux";
import { createAgent } from "@/store/agentsSlice";
import AgentForm from "@/components/agents/AgentForm";

export default function NewAgentPage() {
  const dispatch = useDispatch();
  const router = useRouter();
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);

  async function handleSubmit(values) {
    setSubmitting(true);
    setError(null);
    try {
      const agent = await dispatch(createAgent(values)).unwrap();
      router.push(`/agents/${agent.id}`);
    } catch (err) {
      setError(err.message);
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main>
      <div className="top-nav">
        <h1>New agent</h1>
        <div className="top-nav-links">
          <Link href="/agents">← Agents</Link>
        </div>
      </div>
      <AgentForm onSubmit={handleSubmit} submitLabel="Create agent" submitting={submitting} error={error} />
    </main>
  );
}
