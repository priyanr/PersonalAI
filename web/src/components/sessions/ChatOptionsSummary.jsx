"use client";

import { summarize } from "@/lib/chatOptions";

/** One-line readout of a chat's options; the whole line opens the options sheet. */
export default function ChatOptionsSummary({ options, skills, onEdit }) {
  const s = summarize(options, skills);

  return (
    <button type="button" className="options-summary" onClick={onEdit} aria-haspopup="dialog">
      <span className="options-summary-runs">
        {s.model && (
          <span className="options-run options-run-model">
            <b>Model</b> {s.model}
          </span>
        )}
        {s.toolsOff ? (
          <span className="options-run">
            <b>Tools</b> off
          </span>
        ) : (
          <span className="options-run">
            <b>Tools</b> {s.toolCount} of {s.toolTotal}
            {s.outside.length > 0 && <span className="options-run-attn">{s.outside.join(", ")}</span>}
          </span>
        )}
        {s.skillCount > 0 && (
          <span className="options-run">
            <b>Skills</b> {s.skillCount}
          </span>
        )}
        {s.thinking && (
          <span className="options-run">
            <b>Thinking</b> {s.thinking}
          </span>
        )}
        {s.reasoning && (
          <span className="options-run">
            <b>Reasoning</b> {s.reasoning}
          </span>
        )}
        {s.context && (
          <span className="options-run">
            <b>Context</b> {s.context}
          </span>
        )}
      </span>
      <span className="options-summary-edit">Edit options</span>
    </button>
  );
}
