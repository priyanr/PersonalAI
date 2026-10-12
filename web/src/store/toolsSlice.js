import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { apiGet, apiPatch } from "@/lib/api";

export const fetchTools = createAsyncThunk("tools/fetchAll", () => apiGet("/api/tools"));

export const updateToolConfig = createAsyncThunk("tools/updateConfig", ({ name, values }) =>
  apiPatch(`/api/tools/${name}`, { values })
);

const initialState = {
  items: [],
  listStatus: "idle",
  error: null
};

const toolsSlice = createSlice({
  name: "tools",
  initialState,
  extraReducers(builder) {
    builder
      .addCase(fetchTools.pending, (state) => {
        state.listStatus = "loading";
      })
      .addCase(fetchTools.fulfilled, (state, action) => {
        state.listStatus = "succeeded";
        state.items = action.payload;
      })
      .addCase(fetchTools.rejected, (state, action) => {
        state.listStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(updateToolConfig.fulfilled, (state, action) => {
        const index = state.items.findIndex((t) => t.name === action.payload.name);
        if (index >= 0) state.items[index] = action.payload;
        else state.items.push(action.payload);
      });
  }
});

export default toolsSlice.reducer;
