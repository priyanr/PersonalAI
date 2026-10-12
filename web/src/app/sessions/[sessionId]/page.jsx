"use client";

import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import Link from "next/link";
import { useDispatch, useSelector } from "react-redux";
import { fetchSession, sessionOpened, clearCurrentSession } from "@/store/sessionsSlice";
import { selectChatState } from "@/store/chatSlice";
import { useChatStream } from "@/hooks/useChatStream";
import { useSandboxStatus } from "@/hooks/useSandboxStatus";
import MessageList from "@/components/chat/MessageList";
import ChatInput from "@/components/chat/ChatInput";
import ApprovalPrompt from "@/components/chat/ApprovalPrompt";
import SessionSettingsPanel from "@/components/sessions/SessionSettingsPanel";
import SessionTitleEditor from "@/components/sessions/SessionTitleEditor";
import SandboxFileBrowser from "@/components/sandbox/SandboxFileBrowser";
import { formatTokens, tokenBreakdown } from "@/lib/format";

export default function ChatSessionPage() {
  const { sessionId } = useParams();
  const dispatch = useDispatch();
  const { current, currentStatus, error } = useSelector((state) => state.sessions);
  const { canBrowse: canBrowseSandbox, refresh: refreshSandboxStatus } = useSandboxStatus(sessionId);
  // The sandbox is usually created mid-turn by the model's first sandbox call, long after the mount-time
  // status check -- re-check as soon as one returns, and again when the turn ends as a backstop.
  const { sendMessage, resolveApproval, compact, attach, stop } = useChatStream(sessionId, {
    onToolResult: (event) => {
      if (event.toolName === "sandbox") refreshSandboxStatus();
    },
    onSettled: refreshSandboxStatus
  });
  const isStreaming = useSelector((state) => selectChatState(state, sessionId).isStreaming);
  const [sandboxBrowserOpen, setSandboxBrowserOpen] = useState(false);
  const [compacting, setCompacting] = useState(false);

  useEffect(() => {
    dispatch(sessionOpened(sessionId));
    dispatch(fetchSession(sessionId));
    return () => dispatch(clearCurrentSession());
  }, [dispatch, sessionId]);

  // The turn outlives the page (it runs server-side), so after a reload re-join it instead of offering
  // a fresh send. attach() no-ops when this tab is already streaming it.
  const isTurnRunning = current?.id === sessionId && current.isTurnRunning;
  useEffect(() => {
    if (isTurnRunning) attach();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isTurnRunning, sessionId]);

  // Never render another session's data under this URL (e.g. before the mount effect has run).
  const loaded = current?.id === sessionId;
  if (currentStatus === "failed") return <main className="error-text">Failed to load session: {error}</main>;
  if (!loaded) return <main>Loading chat...</main>;

  const hasPendingApprovals = current.pendingApprovals.length > 0;
  const inputTokens = current.totalInputTokens ?? 0;
  const outputTokens = current.totalOutputTokens ?? 0;

  async function handleCompact() {
    setCompacting(true);
    try {
      await compact();
    } finally {
      setCompacting(false);
    }
  }

  return (
    <main className="chat-main">
      <div className="chat-header">
        <div>
          <Link href="/sessions">← All chats</Link>
          <SessionTitleEditor session={current} />
        </div>
        <div className="chat-header-actions">
          <span className="muted">
            {current.totalLlmCalls ?? 0} LLM {current.totalLlmCalls === 1 ? "call" : "calls"} ·{" "}
            <span title={tokenBreakdown(inputTokens, outputTokens)}>
              {formatTokens(inputTokens + outputTokens)} tokens
            </span>
          </span>
          <button
            type="button"
            className="secondary"
            onClick={handleCompact}
            disabled={isStreaming || hasPendingApprovals || current.messages.length === 0}
            title="Summarize the conversation so far to free up context. Messages stay visible here."
          >
            {compacting ? "Compacting..." : "Compact"}
          </button>
          {canBrowseSandbox && (
            <button type="button" className="secondary" onClick={() => setSandboxBrowserOpen(true)}>
              Sandbox files
            </button>
          )}
        </div>
      </div>

      <SessionSettingsPanel session={current} />

      <MessageList sessionId={sessionId} messages={current.messages} />

      {current.pendingApprovals.map((invocation) => (
        <ApprovalPrompt key={invocation.id} invocation={invocation} onResolve={resolveApproval} disabled={isStreaming} />
      ))}

      <ChatInput sessionId={sessionId} onSend={sendMessage} onStop={stop} disabled={hasPendingApprovals} />

      {sandboxBrowserOpen && (
        <SandboxFileBrowser
          ownerId={sessionId}
          onClose={() => {
            setSandboxBrowserOpen(false);
            refreshSandboxStatus();
          }}
        />
      )}
    </main>
  );
}
