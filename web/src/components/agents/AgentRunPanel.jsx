"use client";

import { useState } from "react";
import Link from "next/link";
import { streamSse } from "@/lib/sse";
import { formatTokens, tokenBreakdown } from "@/lib/format";

export default function AgentRunPanel({ agentId, onRunComplete }) {
  const [running, setRunning] = useState(false);
  const [runId, setRunId] = useState(null);
  const [llmCalls, setLlmCalls] = useState(0);
  const [tokens, setTokens] = useState({ input: 0, output: 0 });
  const [text, setText] = useState("");
  const [toolEvents, setToolEvents] = useState([]);
  const [error, setError] = useState(null);

  async function handleRun() {
    setRunning(true);
    setRunId(null);
    setLlmCalls(0);
    setTokens({ input: 0, output: 0 });
    setText("");
    setToolEvents([]);
    setError(null);
    try {
      await streamSse(`/api/agents/${agentId}/run`, {}, (event) => {
        switch (event.type) {
          case "run-started":
            setRunId(event.runId);
            break;
          case "llm-call":
            setLlmCalls(event.totalCalls);
            break;
          case "token-usage":
            setTokens({ input: event.totalInputTokens, output: event.totalOutputTokens });
            break;
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
      onRunComplete?.();
    }
  }

  return (
    <div className="skill-run-panel">
      <h2>Run</h2>
      <div className="skill-run-form">
        <button type="button" onClick={handleRun} disabled={running}>
          {running ? "Running..." : "Run now"}
        </button>
        {llmCalls > 0 && (
          <span className="muted">
            {llmCalls} LLM {llmCalls === 1 ? "call" : "calls"} ·{" "}
            <span title={tokenBreakdown(tokens.input, tokens.output)}>
              {formatTokens(tokens.input + tokens.output)} tokens
            </span>
          </span>
        )}
        {runId && (
          <Link href={`/agents/${agentId}/runs/${runId}`} className="muted">
            View this run in history →
          </Link>
        )}
      </div>

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
