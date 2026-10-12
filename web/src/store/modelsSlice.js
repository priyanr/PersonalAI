import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { apiDelete, apiGet, apiPut } from "@/lib/api";

export const fetchModels = createAsyncThunk("models/fetchAll", (providerKey = "ollama") =>
  apiGet(`/api/models?providerKey=${encodeURIComponent(providerKey)}`)
);

export const fetchModelSettingFields = createAsyncThunk("models/fetchSettingFields", (providerKey) =>
  apiGet(`/api/models/settings-fields?providerKey=${encodeURIComponent(providerKey)}`)
);

// Model names go in the query string: they contain ':' and '/' (e.g. "llama3.1:8b").
function settingsPath(providerKey, model) {
  return `/api/models/settings?providerKey=${encodeURIComponent(providerKey)}&model=${encodeURIComponent(model)}`;
}

export const saveModelSettings = createAsyncThunk("models/saveSettings", ({ providerKey, model, values }) =>
  apiPut(settingsPath(providerKey, model), { values })
);

export const resetModelSettings = createAsyncThunk("models/resetSettings", ({ providerKey, model }) =>
  apiDelete(settingsPath(providerKey, model))
);

const initialState = {
  items: [],
  // Provider the current `items` (and any in-flight request) belong to — the list is provider-scoped.
  itemsProviderKey: null,
  listStatus: "idle",
  error: null,
  // Keyed by model name: { status: 'pulling'|'success'|'error', statusText, total, completed, error }
  pulls: {},
  // Keyed by provider key: the per-model settings that provider honors (rendered by the Configure dialog).
  settingFields: {}
};

const modelsSlice = createSlice({
  name: "models",
  initialState,
  reducers: {
    pullStarted(state, action) {
      const name = action.payload;
      state.pulls[name] = { status: "pulling", statusText: "starting...", total: null, completed: null, error: null };
    },
    pullProgress(state, action) {
      const { name, status, total, completed } = action.payload;
      state.pulls[name] = {
        ...state.pulls[name],
        status: "pulling",
        statusText: status,
        total: total ?? state.pulls[name]?.total ?? null,
        completed: completed ?? state.pulls[name]?.completed ?? null
      };
    },
    pullSucceeded(state, action) {
      const name = action.payload;
      state.pulls[name] = { ...state.pulls[name], status: "success", statusText: "success" };
    },
    pullFailed(state, action) {
      const { name, error } = action.payload;
      state.pulls[name] = { ...state.pulls[name], status: "error", error };
    }
  },
  extraReducers(builder) {
    builder
      .addCase(fetchModels.pending, (state, action) => {
        const providerKey = action.meta.arg ?? "ollama";
        // Drop the previous provider's models so they can't be picked under the new provider.
        if (state.itemsProviderKey !== providerKey) state.items = [];
        state.itemsProviderKey = providerKey;
        state.listStatus = "loading";
      })
      .addCase(fetchModels.fulfilled, (state, action) => {
        // Ignore a late response for a provider the user has already switched away from.
        if ((action.meta.arg ?? "ollama") !== state.itemsProviderKey) return;
        state.listStatus = "succeeded";
        state.items = action.payload;
      })
      .addCase(fetchModels.rejected, (state, action) => {
        if ((action.meta.arg ?? "ollama") !== state.itemsProviderKey) return;
        state.listStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(fetchModelSettingFields.fulfilled, (state, action) => {
        state.settingFields[action.meta.arg] = action.payload;
      })
      .addCase(saveModelSettings.fulfilled, (state, action) => {
        const { providerKey, model } = action.meta.arg;
        if (providerKey !== state.itemsProviderKey) return;
        const item = state.items.find((m) => m.name === model);
        if (item) item.settings = action.payload;
      })
      .addCase(resetModelSettings.fulfilled, (state, action) => {
        const { providerKey, model } = action.meta.arg;
        if (providerKey !== state.itemsProviderKey) return;
        const item = state.items.find((m) => m.name === model);
        if (item) item.settings = {};
      });
  }
});

export const { pullStarted, pullProgress, pullSucceeded, pullFailed } = modelsSlice.actions;
export default modelsSlice.reducer;
