import { createAsyncThunk, createSlice } from "@reduxjs/toolkit";
import { getAuthStatus, getMe, startLogin } from "@/lib/auth";

// If auth is enabled and there's no valid token (never logged in, or the refresh token was
// rejected), getMe() throws a 401. We send the browser to login and never resolve this thunk --
// exactly what AuthGate wants (keep rendering nothing while the browser navigates away). Any other
// failure (the login provider briefly unavailable) keeps the stored session and renders the app;
// its own requests will surface the error and retry the refresh on their next call.
export const fetchAuthState = createAsyncThunk("auth/fetchState", async () => {
  const status = await getAuthStatus();
  if (!status.enabled) return { enabled: false, subject: null };

  try {
    const me = await getMe();
    return { enabled: true, subject: me.subject };
  } catch (err) {
    if (err.status !== 401) return { enabled: true, subject: null };
    startLogin();
    return new Promise(() => {});
  }
});

const initialState = {
  status: "idle",
  enabled: false,
  subject: null
};

const authSlice = createSlice({
  name: "auth",
  initialState,
  extraReducers(builder) {
    builder
      .addCase(fetchAuthState.pending, (state) => {
        state.status = "loading";
      })
      .addCase(fetchAuthState.fulfilled, (state, action) => {
        state.status = "ready";
        state.enabled = action.payload.enabled;
        state.subject = action.payload.subject;
      });
  }
});

export default authSlice.reducer;
