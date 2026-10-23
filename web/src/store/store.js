import { configureStore } from "@reduxjs/toolkit";
import sessionsReducer from "./sessionsSlice";
import modelsReducer from "./modelsSlice";
import chatReducer from "./chatSlice";
import providersReducer from "./providersSlice";
import secretsReducer from "./secretsSlice";
import skillsReducer from "./skillsSlice";
import agentsReducer from "./agentsSlice";
import authReducer from "./authSlice";
import toolsReducer from "./toolsSlice";
import sshServersReducer from "./sshServersSlice";

export function makeStore() {
  return configureStore({
    reducer: {
      sessions: sessionsReducer,
      models: modelsReducer,
      chat: chatReducer,
      providers: providersReducer,
      secrets: secretsReducer,
      skills: skillsReducer,
      agents: agentsReducer,
      auth: authReducer,
      tools: toolsReducer,
      sshServers: sshServersReducer
    }
  });
}
