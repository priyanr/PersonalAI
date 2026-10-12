"use client";

import { useState } from "react";
import { streamSse } from "@/lib/sse";

export default function SkillRunPanel({ skillId }) {
  const [input, setInput] = useState("");
  const [running, setRunning] = useState(false);
  const [text, setText] = useState("");
  const [toolEvents, setToolEvents] = useState([]);
  const [error, setError] = useState(null);

  async function handleRun(e) {
    e.preventDefault();
    setRunning(true);
    setText("");
    setToolEvents([]);
    setError(null);
    try {
      await streamSse(`/api/skills/${skillId}/run`, { input: input.trim() || null }, (event) => {
        switch (event.type) {
          case "token":
            setText((prev) => prev + event.text);
            break;
          case "tool-result":
            setToolEvents((prev) => [...prev, event]);
            break;
          case "error":
            setError(event.message);
            break;
          default:
            break;
        }
      });
    } catch (err) {
      setError(err.message);
    } finally {
      setRunning(false);
    }
  }

  return (
    <div className="skill-run-panel">
      <h2>Run</h2>
      <form onSubmit={handleRun} className="skill-run-form">
        <textarea
          rows={2}
          placeholder="Optional input for this run (leave blank to just execute the prompt)"
          value={input}
          disabled={running}
          onChange={(e) => setInput(e.target.value)}
        />
        <button type="submit" disabled={running}>
          {running ? "Running..." : "Run skill"}
        </button>
      </form>

      {toolEvents.length > 0 && (
        <div className="tool-activity">
          {toolEvents.map((evt, i) => (
            <div key={i} className={`tool-activity-item${evt.isError ? " is-error" : ""}`}>
              <strong>{evt.toolName}</strong>: {evt.resultContent}
            </div>
          ))}
        </div>
      )}

      {text && <div className="skill-run-output">{text}</div>}
      {error && <p className="error-text">{error}</p>}
    </div>
  );
}
