"use client";

import { useState } from "react";
import { useSelector } from "react-redux";
import { selectChatState } from "@/store/chatSlice";

export default function ChatInput({ sessionId, onSend, onStop, disabled }) {
  const [text, setText] = useState("");
  const { isStreaming, stopRequested } = useSelector((state) => selectChatState(state, sessionId));
  const blocked = disabled || isStreaming;

  function handleSubmit(e) {
    e.preventDefault();
    const trimmed = text.trim();
    if (!trimmed || blocked) return;
    setText("");
    onSend(trimmed);
  }

  return (
    <form className="chat-input" onSubmit={handleSubmit}>
      <textarea
        rows={2}
        placeholder={blocked ? "Waiting for a response..." : "Type a message..."}
        value={text}
        disabled={blocked}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter" && !e.shiftKey) {
            handleSubmit(e);
          }
        }}
      />
      {isStreaming ? (
        <button type="button" className="secondary" onClick={onStop} disabled={stopRequested}>
          {stopRequested ? "Stopping..." : "Stop"}
        </button>
      ) : (
        <button type="submit" disabled={blocked || !text.trim()}>
          Send
        </button>
      )}
    </form>
  );
}
