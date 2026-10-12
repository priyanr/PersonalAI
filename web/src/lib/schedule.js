import { AgentScheduleKind } from "@/lib/enums";

// Human-readable "next run" text for a scheduled agent, or null when nothing is scheduled
// (Manual, paused, or a one-off that already ran).
export function formatNextRun(agent) {
  if (agent.scheduleKind === AgentScheduleKind.Manual || !agent.enabled || !agent.nextRunAt) return null;
  return new Date(agent.nextRunAt).toLocaleString();
}
