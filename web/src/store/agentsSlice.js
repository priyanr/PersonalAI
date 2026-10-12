import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { apiDelete, apiGet, apiPatch, apiPost } from "@/lib/api";

export const fetchAgents = createAsyncThunk("agents/fetchAll", () => apiGet("/api/agents"));

export const createAgent = createAsyncThunk("agents/create", (agent) => apiPost("/api/agents", agent));

export const updateAgent = createAsyncThunk("agents/update", ({ id, ...agent }) =>
  apiPatch(`/api/agents/${id}`, agent)
);

export const deleteAgent = createAsyncThunk("agents/delete", async ({ id }) => {
  await apiDelete(`/api/agents/${id}`);
  return id;
});

const initialState = {
  items: [],
  listStatus: "idle",
  error: null
};

const agentsSlice = createSlice({
  name: "agents",
  initialState,
  extraReducers(builder) {
    builder
      .addCase(fetchAgents.pending, (state) => {
        state.listStatus = "loading";
      })
      .addCase(fetchAgents.fulfilled, (state, action) => {
        state.listStatus = "succeeded";
        state.items = action.payload;
      })
      .addCase(fetchAgents.rejected, (state, action) => {
        state.listStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(createAgent.fulfilled, (state, action) => {
        state.items.unshift(action.payload);
      })
      .addCase(updateAgent.fulfilled, (state, action) => {
        const index = state.items.findIndex((a) => a.id === action.payload.id);
        if (index >= 0) state.items[index] = action.payload;
      })
      .addCase(deleteAgent.fulfilled, (state, action) => {
        state.items = state.items.filter((a) => a.id !== action.payload);
      });
  }
});

export default agentsSlice.reducer;
