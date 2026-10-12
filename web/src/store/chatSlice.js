import { createSlice } from "@reduxjs/toolkit";

const initialState = {
  bySession: {}
};

const emptySessionState = {
  isStreaming: false,
  streamingText: "",
  // The model's current thinking phase (live-only, never persisted). Cleared once it starts answering or
  // a tool result arrives, and capped to its tail so a long reasoning run can't grow state without bound.
  reasoningText: "",
  toolEvents: [],
  error: null,
  // Sequence number of the last message that predates the running turn (from its "turn-started" event).
  // While streaming, persisted messages above it are hidden — the stream already shows them live.
  turnAfterSequence: null,
  stopRequested: false
};

const REASONING_TAIL_CHARS = 4000;

function getSession(state, sessionId) {
  if (!state.bySession[sessionId]) {
    state.bySession[sessionId] = { ...emptySessionState };
  }
  return state.bySession[sessionId];
}

const chatSlice = createSlice({
  name: "chat",
  initialState,
  reducers: {
    streamStarted(state, action) {
      const session = getSession(state, action.payload.sessionId);
      session.isStreaming = true;
      session.streamingText = "";
      session.reasoningText = "";
      session.toolEvents = [];
      session.error = null;
      session.turnAfterSequence = null;
      session.stopRequested = false;
    },
    turnStarted(state, action) {
      getSession(state, action.payload.sessionId).turnAfterSequence = action.payload.afterSequenceNumber;
    },
    stopRequested(state, action) {
      getSession(state, action.payload.sessionId).stopRequested = true;
    },
    tokenReceived(state, action) {
      const session = getSession(state, action.payload.sessionId);
      session.streamingText += action.payload.text;
      session.reasoningText = "";
    },
    reasoningReceived(state, action) {
      const session = getSession(state, action.payload.sessionId);
      const text = session.reasoningText + action.payload.text;
      session.reasoningText = text.length > REASONING_TAIL_CHARS ? text.slice(-REASONING_TAIL_CHARS) : text;
    },
    toolResultReceived(state, action) {
      const { sessionId, ...event } = action.payload;
      const session = getSession(state, sessionId);
      session.toolEvents.push({ type: "tool-result", ...event });
      session.reasoningText = "";
    },
    toolProgressReceived(state, action) {
      const { sessionId, ...event } = action.payload;
      getSession(state, sessionId).toolEvents.push({ type: "tool-progress", ...event });
    },
    approvalRequired(state, action) {
      const { sessionId, ...event } = action.payload;
      getSession(state, sessionId).toolEvents.push({ type: "approval-required", ...event });
    },
    streamDone(state, action) {
      const session = getSession(state, action.payload.sessionId);
      session.isStreaming = false;
      session.reasoningText = "";
    },
    streamError(state, action) {
      const session = getSession(state, action.payload.sessionId);
      session.isStreaming = false;
      session.reasoningText = "";
      session.error = action.payload.message;
    }
  }
});

export const { streamStarted, turnStarted, stopRequested, tokenReceived, reasoningReceived, toolResultReceived, toolProgressReceived, approvalRequired, streamDone, streamError } =
  chatSlice.actions;

const emptySelectorResult = emptySessionState;

export function selectChatState(state, sessionId) {
  return state.chat.bySession[sessionId] || emptySelectorResult;
}

export default chatSlice.reducer;
