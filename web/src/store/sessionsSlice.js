import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { apiGet, apiPatch, apiPost } from "@/lib/api";

export const SESSIONS_PAGE_SIZE = 20;

export const fetchSessions = createAsyncThunk("sessions/fetchAll", ({ search = "", page = 1 } = {}) => {
  const params = new URLSearchParams({ page: String(page), pageSize: String(SESSIONS_PAGE_SIZE) });
  if (search.trim()) params.set("search", search.trim());
  return apiGet(`/api/sessions?${params}`);
});

export const renameSession = createAsyncThunk("sessions/rename", ({ sessionId, title }) =>
  apiPatch(`/api/sessions/${sessionId}/title`, { title })
);

export const createSession = createAsyncThunk(
  "sessions/create",
  ({ title, modelName, providerKey, toolsEnabled, numCtx, trimHistoryEnabled, thinkingEnabled, reasoningEffort, maxLlmCalls, enabledSkillIds, toolFlags }) =>
    apiPost("/api/sessions", { title, modelName, providerKey, toolsEnabled, numCtx, trimHistoryEnabled, thinkingEnabled, reasoningEffort, maxLlmCalls, enabledSkillIds, toolFlags })
);

export const fetchSession = createAsyncThunk("sessions/fetchOne", (sessionId) =>
  apiGet(`/api/sessions/${sessionId}`)
);

export const updateSessionSettings = createAsyncThunk(
  "sessions/updateSettings",
  // providerKey / modelName are optional — send them only when switching models; omitted keeps the current one.
  ({ sessionId, toolsEnabled, numCtx, trimHistoryEnabled, thinkingEnabled, reasoningEffort, maxLlmCalls, enabledSkillIds, toolFlags, providerKey, modelName }) =>
    apiPatch(`/api/sessions/${sessionId}/settings`, { toolsEnabled, numCtx, trimHistoryEnabled, thinkingEnabled, reasoningEffort, maxLlmCalls, enabledSkillIds, toolFlags, providerKey, modelName })
);

const initialState = {
  items: [],
  totalCount: 0,
  listStatus: "idle",
  listRequestId: null,
  // Kept in the store (not component state) so coming back from a chat restores the search and page.
  listQuery: { search: "", page: 1 },
  // The session the chat page is showing. Background streams of other sessions keep refetching theirs,
  // and those responses must not replace `current`.
  currentId: null,
  current: null,
  currentStatus: "idle",
  error: null
};

const sessionsSlice = createSlice({
  name: "sessions",
  initialState,
  reducers: {
    sessionSearchChanged(state, action) {
      state.listQuery = { search: action.payload, page: 1 };
    },
    sessionPageChanged(state, action) {
      state.listQuery = { ...state.listQuery, page: action.payload };
    },
    sessionOpened(state, action) {
      state.currentId = action.payload;
      if (state.current && state.current.id !== action.payload) {
        state.current = null;
        state.currentStatus = "idle";
      }
    },
    clearCurrentSession(state) {
      state.currentId = null;
      state.current = null;
      state.currentStatus = "idle";
    },
    // Live-streamed "llm-call" events keep the header count current mid-turn, before the end-of-turn refetch.
    llmCallCountUpdated(state, action) {
      if (state.current && state.current.id === action.payload.sessionId) {
        state.current.totalLlmCalls = action.payload.totalCalls;
      }
    },
    tokenUsageUpdated(state, action) {
      if (state.current && state.current.id === action.payload.sessionId) {
        state.current.totalInputTokens = action.payload.totalInputTokens;
        state.current.totalOutputTokens = action.payload.totalOutputTokens;
      }
    }
  },
  extraReducers(builder) {
    builder
      // Only the latest request may land: typing a search fires several fetches, and an older, slower
      // response must not overwrite a newer one.
      .addCase(fetchSessions.pending, (state, action) => {
        state.listStatus = "loading";
        state.listRequestId = action.meta.requestId;
      })
      .addCase(fetchSessions.fulfilled, (state, action) => {
        if (action.meta.requestId !== state.listRequestId) return;
        state.listStatus = "succeeded";
        state.items = action.payload.items;
        state.totalCount = action.payload.totalCount;
      })
      .addCase(fetchSessions.rejected, (state, action) => {
        if (action.meta.requestId !== state.listRequestId) return;
        state.listStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(createSession.fulfilled, (state, action) => {
        state.items.unshift(action.payload);
        state.totalCount += 1;
      })
      .addCase(renameSession.fulfilled, (state, action) => {
        const { id, title } = action.payload;
        if (state.current && state.current.id === id) state.current.title = title;
        const item = state.items.find((s) => s.id === id);
        if (item) item.title = title;
      })
      .addCase(fetchSession.pending, (state, action) => {
        if (action.meta.arg !== state.currentId) return;
        state.currentStatus = "loading";
      })
      .addCase(fetchSession.fulfilled, (state, action) => {
        if (action.meta.arg !== state.currentId) return;
        state.currentStatus = "succeeded";
        state.current = action.payload;
      })
      .addCase(fetchSession.rejected, (state, action) => {
        if (action.meta.arg !== state.currentId) return;
        state.currentStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(updateSessionSettings.fulfilled, (state, action) => {
        if (state.current && state.current.id === action.payload.id) {
          Object.assign(state.current, action.payload);
        }
        const item = state.items.find((s) => s.id === action.payload.id);
        if (item) {
          item.providerKey = action.payload.providerKey;
          item.modelName = action.payload.modelName;
        }
      });
  }
});

export const { sessionSearchChanged, sessionPageChanged, sessionOpened, clearCurrentSession, llmCallCountUpdated, tokenUsageUpdated } = sessionsSlice.actions;
export default sessionsSlice.reducer;
