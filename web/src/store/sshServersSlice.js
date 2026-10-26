import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { apiDelete, apiGet, apiPatch, apiPost } from "@/lib/api";

export const fetchServerGroups = createAsyncThunk("sshServers/fetchGroups", () => apiGet("/api/ssh/server-groups"));
export const fetchServers = createAsyncThunk("sshServers/fetchServers", () => apiGet("/api/ssh/servers"));

export const createServerGroup = createAsyncThunk("sshServers/createGroup", (group) =>
  apiPost("/api/ssh/server-groups", group)
);

export const updateServerGroup = createAsyncThunk("sshServers/updateGroup", ({ id, ...group }) =>
  apiPatch(`/api/ssh/server-groups/${id}`, group)
);

export const deleteServerGroup = createAsyncThunk("sshServers/deleteGroup", async ({ id }) => {
  await apiDelete(`/api/ssh/server-groups/${id}`);
  return id;
});

export const createServer = createAsyncThunk("sshServers/createServer", (server) => apiPost("/api/ssh/servers", server));

export const updateServer = createAsyncThunk("sshServers/updateServer", ({ id, ...server }) =>
  apiPatch(`/api/ssh/servers/${id}`, server)
);

export const deleteServer = createAsyncThunk("sshServers/deleteServer", async ({ id }) => {
  await apiDelete(`/api/ssh/servers/${id}`);
  return id;
});

// Test-connection and host-key reset change the server's stored state (LastSeenAt/LastError/pin), so
// re-fetch the single server afterwards instead of patching fields client-side.
export const testServerConnection = createAsyncThunk("sshServers/testConnection", async ({ id }) => {
  const result = await apiPost(`/api/ssh/servers/${id}/test-connection`, {});
  const server = await apiGet(`/api/ssh/servers/${id}`);
  return { result, server };
});

export const resetServerHostKey = createAsyncThunk("sshServers/resetHostKey", async ({ id }) => {
  await apiPost(`/api/ssh/servers/${id}/reset-host-key`, {});
  return apiGet(`/api/ssh/servers/${id}`);
});

const initialState = {
  groups: [],
  servers: [],
  listStatus: "idle",
  error: null
};

function upsertServer(state, server) {
  const index = state.servers.findIndex((t) => t.id === server.id);
  if (index >= 0) state.servers[index] = server;
  else state.servers.push(server);
}

function adjustGroupCount(state, groupId, delta) {
  const group = state.groups.find((g) => g.id === groupId);
  if (group) group.serverCount = Math.max(0, group.serverCount + delta);
}

const sshServersSlice = createSlice({
  name: "sshServers",
  initialState,
  extraReducers(builder) {
    builder
      .addCase(fetchServerGroups.pending, (state) => {
        state.listStatus = "loading";
      })
      .addCase(fetchServerGroups.fulfilled, (state, action) => {
        state.listStatus = "succeeded";
        state.groups = action.payload;
      })
      .addCase(fetchServerGroups.rejected, (state, action) => {
        state.listStatus = "failed";
        state.error = action.error.message;
      })
      .addCase(fetchServers.fulfilled, (state, action) => {
        state.servers = action.payload;
      })
      .addCase(createServerGroup.fulfilled, (state, action) => {
        state.groups.push(action.payload);
        state.groups.sort((a, b) => a.name.localeCompare(b.name));
      })
      .addCase(updateServerGroup.fulfilled, (state, action) => {
        const index = state.groups.findIndex((g) => g.id === action.payload.id);
        if (index >= 0) state.groups[index] = action.payload;
        state.groups.sort((a, b) => a.name.localeCompare(b.name));
      })
      .addCase(deleteServerGroup.fulfilled, (state, action) => {
        state.groups = state.groups.filter((g) => g.id !== action.payload);
        state.servers = state.servers.filter((t) => t.groupId !== action.payload);
      })
      .addCase(createServer.fulfilled, (state, action) => {
        upsertServer(state, action.payload);
        adjustGroupCount(state, action.payload.groupId, 1);
      })
      .addCase(updateServer.fulfilled, (state, action) => {
        const previous = state.servers.find((t) => t.id === action.payload.id);
        if (previous && previous.groupId !== action.payload.groupId) {
          adjustGroupCount(state, previous.groupId, -1);
          adjustGroupCount(state, action.payload.groupId, 1);
        }
        upsertServer(state, action.payload);
      })
      .addCase(deleteServer.fulfilled, (state, action) => {
        const previous = state.servers.find((t) => t.id === action.payload);
        if (previous) adjustGroupCount(state, previous.groupId, -1);
        state.servers = state.servers.filter((t) => t.id !== action.payload);
      })
      .addCase(testServerConnection.fulfilled, (state, action) => {
        upsertServer(state, action.payload.server);
      })
      .addCase(resetServerHostKey.fulfilled, (state, action) => {
        upsertServer(state, action.payload);
      });
  }
});

export default sshServersSlice.reducer;
