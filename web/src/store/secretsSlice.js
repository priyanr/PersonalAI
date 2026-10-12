import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { apiDelete, apiGet, apiPost, apiPut } from "@/lib/api";

export const fetchSecrets = createAsyncThunk("secrets/fetchAll", () => apiGet("/api/secrets"));

export const createSecret = createAsyncThunk("secrets/create", ({ key, value }) =>
  apiPost("/api/secrets", { key, value })
);

export const updateSecret = createAsyncThunk("secrets/update", ({ key, value }) =>
  apiPut(`/api/secrets/${encodeURIComponent(key)}`, { value })
);

export const deleteSecret = createAsyncThunk("secrets/delete", async ({ key }) => {
  await apiDelete(`/api/secrets/${encodeURIComponent(key)}`);
  return key;
});

const initialState = {
  items: [],
  listStatus: "idle",
  error: null
};

const secretsSlice = createSlice({
  name: "secrets",
  initialState,
  extraReducers(builder) {
    builder
      .addCase(fetchSecrets.pending, (state) => {
        state.listStatus = "loading";
      })
      .addCase(fetchSecrets.fulfilled, (state, action) => {
        state.listStatus = "succeeded";
        state.items = action.payload;
      })
      .addCase(fetchSecrets.rejected, (state, action) => {
        state.listStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(createSecret.fulfilled, (state, action) => {
        state.items.push(action.payload);
        state.items.sort((a, b) => a.key.localeCompare(b.key));
      })
      .addCase(updateSecret.fulfilled, (state, action) => {
        const index = state.items.findIndex((s) => s.key === action.payload.key);
        if (index >= 0) state.items[index] = action.payload;
      })
      .addCase(deleteSecret.fulfilled, (state, action) => {
        state.items = state.items.filter((s) => s.key !== action.payload);
      });
  }
});

export default secretsSlice.reducer;
