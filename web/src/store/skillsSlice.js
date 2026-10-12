import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { apiDelete, apiGet, apiPatch, apiPost } from "@/lib/api";

export const fetchSkills = createAsyncThunk("skills/fetchAll", () => apiGet("/api/skills"));

export const fetchAvailableTools = createAsyncThunk("skills/fetchAvailableTools", () =>
  apiGet("/api/skills/available-tools")
);

export const createSkill = createAsyncThunk("skills/create", (skill) => apiPost("/api/skills", skill));

export const updateSkill = createAsyncThunk("skills/update", ({ id, ...skill }) =>
  apiPatch(`/api/skills/${id}`, skill)
);

export const deleteSkill = createAsyncThunk("skills/delete", async ({ id }) => {
  await apiDelete(`/api/skills/${id}`);
  return id;
});

const initialState = {
  items: [],
  listStatus: "idle",
  availableTools: [],
  error: null
};

const skillsSlice = createSlice({
  name: "skills",
  initialState,
  extraReducers(builder) {
    builder
      .addCase(fetchSkills.pending, (state) => {
        state.listStatus = "loading";
      })
      .addCase(fetchSkills.fulfilled, (state, action) => {
        state.listStatus = "succeeded";
        state.items = action.payload;
      })
      .addCase(fetchSkills.rejected, (state, action) => {
        state.listStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(fetchAvailableTools.fulfilled, (state, action) => {
        state.availableTools = action.payload;
      })
      .addCase(createSkill.fulfilled, (state, action) => {
        state.items.unshift(action.payload);
      })
      .addCase(updateSkill.fulfilled, (state, action) => {
        const index = state.items.findIndex((s) => s.id === action.payload.id);
        if (index >= 0) state.items[index] = action.payload;
      })
      .addCase(deleteSkill.fulfilled, (state, action) => {
        state.items = state.items.filter((s) => s.id !== action.payload);
      });
  }
});

export default skillsSlice.reducer;
