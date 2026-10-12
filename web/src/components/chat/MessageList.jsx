"use client";

import { useEffect, useRef } from "react";
import { useSelector } from "react-redux";
import { selectChatState } from "@/store/chatSlice";
import MessageBubble from "./MessageBubble";
import ToolCallActivity from "./ToolCallActivity";

export default function MessageList({ sessionId, messages }) {
  const { isStreaming, streamingText, reasoningText, toolEvents, error, turnAfterSequence } = useSelector((state) =>
    selectChatState(state, sessionId)
  );
  // Messages the running turn has already persisted are also in the live stream below — show them once.
  const visibleMessages =
    isStreaming && turnAfterSequence != null ? messages.filter((m) => m.sequenceNumber <= turnAfterSequence) : messages;
  const bottomRef = useRef(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, streamingText, reasoningText, toolEvents, error]);

  return (
    <div className="message-list">
      {visibleMessages.map((m) => (
        <MessageBubble key={m.id} message={m} />
      ))}

      {isStreaming && (
        <div className="message-bubble bubble-assistant">
          <div className="message-role">Assistant</div>
          {(streamingText || !reasoningText) && <div className="message-content">{streamingText || "..."}</div>}
          {/* After the text: streamingText spans every call of the turn, and this is the current call's thinking. */}
          {reasoningText && (
            <details className="message-reasoning" open>
              <summary>Thinking…</summary>
              <div className="message-reasoning-text">{reasoningText}</div>
            </details>
          )}
        </div>
      )}

      {isStreaming && <ToolCallActivity events={toolEvents} />}

      {error && !isStreaming && <p className="error-text">Error: {error}</p>}

      <div ref={bottomRef} />
    </div>
  );
}
