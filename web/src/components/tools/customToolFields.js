import GmailConnectButton from "./GmailConnectButton";
import SandboxHostStatus from "./SandboxHostStatus";

// Registry mapping a tool field's `customComponentKey` to the React component that
// renders it. Add new entries here as backend-driven tools grow custom UI needs --
// ToolSettingsForm looks components up by key and silently skips unknown ones.
export const customToolFields = {
  gmailOAuthConnect: GmailConnectButton,
  sandboxHostStatus: SandboxHostStatus
};
