import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { apiGet, apiPatch } from "@/lib/api";

export const fetchProviders = createAsyncThunk("providers/fetchAll", () => apiGet("/api/ai-providers"));

export const updateProviderConfig = createAsyncThunk("providers/updateConfig", ({ key, values }) =>
  apiPatch(`/api/ai-providers/${key}`, { values })
);

const initialState = {
  items: [],
  listStatus: "idle",
  error: null
};

const providersSlice = createSlice({
  name: "providers",
  initialState,
  extraReducers(builder) {
    builder
      .addCase(fetchProviders.pending, (state) => {
        state.listStatus = "loading";
      })
      .addCase(fetchProviders.fulfilled, (state, action) => {
        state.listStatus = "succeeded";
        state.items = action.payload;
      })
      .addCase(fetchProviders.rejected, (state, action) => {
        state.listStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(updateProviderConfig.fulfilled, (state, action) => {
        const index = state.items.findIndex((p) => p.key === action.payload.key);
        if (index >= 0) state.items[index] = action.payload;
        else state.items.push(action.payload);
      });
  }
});

export default providersSlice.reducer;
