"use client";

import { useState } from "react";
import { streamSse } from "@/lib/sse";

/**
 * Follow-up chat on a finished run. The agent keeps its tools, so the user can ask it to change what it built (add a
 * missing widget, another job...). Its reply is appended to the run's transcript, which the page already polls, so this
 * only sends the message and reports progress: onStarted/onFinished tell the page to (re)start polling.
 */
export default function RunChat({ agentId, runId, disabled, onStarted, onFinished }) {
  const [message, setMessage] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState(null);

  async function handleSend(e) {
    e.preventDefault();
    const text = message.trim();
    if (!text || sending) return;

    setSending(true);
    setError(null);
    setMessage("");
    try {
      await streamSse(`/api/agents/${agentId}/runs/${runId}/messages`, { message: text }, (event) => {
        if (event.type === "run-started") onStarted?.();
        else if (event.type === "error") setError(event.message);
      });
    } catch (err) {
      setError(err.status === 409 ? "This run is still in progress." : err.message);
      setMessage(text);
    } finally {
      setSending(false);
      onFinished?.();
    }
  }

  return (
    <form className="run-chat" onSubmit={handleSend}>
      <textarea
        value={message}
        onChange={(e) => setMessage(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter" && !e.shiftKey) handleSend(e);
        }}
        rows={2}
        disabled={disabled || sending}
        placeholder="Ask the agent to change something, e.g. “add a widget for the failing jobs”"
      />
      <div className="secret-row-actions">
        <button type="submit" disabled={disabled || sending || !message.trim()}>
          {sending ? "Working..." : "Send"}
        </button>
        {error && <span className="error-text">{error}</span>}
      </div>
    </form>
  );
}
