"use client";

import { useDispatch, useStore } from "react-redux";
import { attachSse, streamSse } from "@/lib/sse";
import { apiPost } from "@/lib/api";
import {
  selectChatState,
  streamStarted,
  turnStarted,
  stopRequested,
  tokenReceived,
  reasoningReceived,
  toolResultReceived,
  toolProgressReceived,
  approvalRequired,
  streamDone,
  streamError
} from "@/store/chatSlice";
import { fetchSession, llmCallCountUpdated, tokenUsageUpdated } from "@/store/sessionsSlice";

// The turn itself runs on the server independent of this connection (ChatTurnRunner): closing the tab or
// reloading only detaches the viewer. `attach` re-joins a running turn (replaying it from the start),
// and `stop` is the only way to actually end one early.
// onToolResult fires for each tool-result event (after it's dispatched); onSettled fires once the stream
// has ended, however it ended. Both are optional.
export function useChatStream(sessionId, { onToolResult, onSettled } = {}) {
  const dispatch = useDispatch();
  const store = useStore();

  async function run(startStream) {
    dispatch(streamStarted({ sessionId }));
    try {
      await startStream((event) => {
        switch (event.type) {
          case "turn-started":
            dispatch(turnStarted({ sessionId, afterSequenceNumber: event.afterSequenceNumber }));
            // Picks up the just-persisted user message; anything newer stays hidden until the turn ends.
            dispatch(fetchSession(sessionId));
            break;
          case "llm-call":
            dispatch(llmCallCountUpdated({ sessionId, totalCalls: event.totalCalls }));
            break;
          case "token-usage":
            dispatch(
              tokenUsageUpdated({
                sessionId,
                totalInputTokens: event.totalInputTokens,
                totalOutputTokens: event.totalOutputTokens
              })
            );
            break;
          case "token":
            dispatch(tokenReceived({ sessionId, text: event.text }));
            break;
          case "reasoning":
            dispatch(reasoningReceived({ sessionId, text: event.text }));
            break;
          case "tool-result":
            dispatch(toolResultReceived({ sessionId, ...event }));
            onToolResult?.(event);
            break;
          case "tool-progress":
            dispatch(toolProgressReceived({ sessionId, ...event }));
            break;
          case "approval-required":
            dispatch(approvalRequired({ sessionId, ...event }));
            break;
          case "done":
            dispatch(streamDone({ sessionId }));
            break;
          case "error":
            dispatch(streamError({ sessionId, message: event.message }));
            break;
          default:
            break;
        }
      });
      // A stream can end without "done" (paused for approval) — never leave the input locked.
      dispatch(streamDone({ sessionId }));
    } catch (err) {
      dispatch(streamError({ sessionId, message: err.message }));
      return err;
    } finally {
      // The database is the source of truth for message history and pending approvals —
      // always resync after a stream ends, regardless of how it ended.
      dispatch(fetchSession(sessionId));
      onSettled?.();
    }
    return null;
  }

  async function sendMessage(text) {
    const err = await run((onEvent) => streamSse(`/api/sessions/${sessionId}/messages`, { text }, onEvent));
    // 409: a turn is already running (started in another tab, or before a reload this page hasn't
    // re-attached to yet) — show that one rather than just an error.
    if (err?.status === 409) await attach();
  }

  function resolveApproval(toolInvocationId, approved) {
    return run((onEvent) => streamSse(`/api/tool-invocations/${toolInvocationId}/resolve`, { approved }, onEvent));
  }

  /** Summarizes the conversation so far so later turns send the model the summary instead of the old messages. */
  async function compact() {
    const err = await run((onEvent) => streamSse(`/api/sessions/${sessionId}/compact`, {}, onEvent));
    if (err?.status === 409) await attach();
  }

  /** Re-joins the session's running turn, if any. No-op when this tab is already streaming it. */
  async function attach() {
    if (selectChatState(store.getState(), sessionId).isStreaming) return;
    await run((onEvent) => attachSse(`/api/sessions/${sessionId}/turn/stream`, onEvent));
  }

  async function stop() {
    dispatch(stopRequested({ sessionId }));
    try {
      await apiPost(`/api/sessions/${sessionId}/turn/stop`);
    } catch {
      // 404: the turn finished on its own between the click and the request — its stream ends anyway.
    }
  }

  return { sendMessage, resolveApproval, compact, attach, stop };
}
