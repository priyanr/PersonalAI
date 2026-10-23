"use client";

export default function RunTranscript({ entries }) {
  if (!entries || entries.length === 0) {
    return <p className="muted">No transcript recorded for this run.</p>;
  }

  return (
    <div className="run-transcript">
      {entries.map((entry, i) => (
        <div
          key={i}
          className={`run-transcript-entry${entry.kind === "skill" || entry.kind === "tool" ? " is-skill" : ""}${entry.kind === "user" ? " is-user" : ""}${entry.isError ? " is-error" : ""}`}
        >
          <span className="run-transcript-entry-label">
            {entry.kind === "skill" && `Skill: ${entry.skillName}`}
            {entry.kind === "tool" && `Tool: ${entry.skillName}`}
            {entry.kind === "user" && "You"}
            {entry.kind !== "skill" && entry.kind !== "tool" && entry.kind !== "user" && "Agent"}
            {entry.isError && " (error)"}
          </span>
          {entry.content}
        </div>
      ))}
    </div>
  );
}
