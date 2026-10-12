"use client";

import Link from "next/link";
import { AGENT_SCHEDULE_KIND_OPTIONS, AgentScheduleKind } from "@/lib/enums";
import { formatNextRun } from "@/lib/schedule";

function scheduleLabel(agent) {
  const kindLabel = AGENT_SCHEDULE_KIND_OPTIONS.find((o) => o.value === agent.scheduleKind)?.label ?? "Manual";
  if (agent.scheduleKind === AgentScheduleKind.Manual) return kindLabel;
  return agent.enabled ? kindLabel : `${kindLabel} (paused)`;
}

export default function AgentList({ agents }) {
  if (agents.length === 0) {
    return <p className="muted">No agents yet. Create one to invoke skills on a schedule.</p>;
  }

  return (
    <ul className="skill-list">
      {agents.map((agent) => (
        <li key={agent.id} className="skill-list-item-wrap">
          <Link href={`/agents/${agent.id}`} className="skill-list-item">
            <span className="skill-name">{agent.name}</span>
            {agent.description && <span className="skill-description">{agent.description}</span>}
            <span className="skill-meta">
              {agent.providerKey} · {agent.modelName} · {scheduleLabel(agent)}
              {formatNextRun(agent) && ` · next run ${formatNextRun(agent)}`}
              {agent.enabledSkillIds.length > 0 && ` · ${agent.enabledSkillIds.length} skill(s)`}
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}
