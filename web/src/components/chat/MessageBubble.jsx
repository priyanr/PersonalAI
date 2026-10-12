import { MessageRole } from "@/lib/enums";
import TerminalOutput from "./TerminalOutput";

const roleLabel = {
  [MessageRole.User]: "You",
  [MessageRole.Assistant]: "Assistant",
  [MessageRole.Tool]: "Tool",
  [MessageRole.System]: "System"
};

// Must match ChatAgentService.CompactionMarkerPrefix.
const COMPACTION_MARKER_PREFIX = "Conversation compacted";

function formatTime(timestamp) {
  if (!timestamp) return null;
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) return null;
  return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

export default function MessageBubble({ message }) {
  if (message.role === MessageRole.System && message.content?.startsWith(COMPACTION_MARKER_PREFIX)) {
    return (
      <div className="compaction-divider" role="separator">
        <span>{message.content}</span>
      </div>
    );
  }

  const roleClass =
    message.role === MessageRole.User
      ? "bubble-user"
      : message.role === MessageRole.Tool
        ? "bubble-tool"
        : message.role === MessageRole.System
          ? "bubble-system"
          : "bubble-assistant";

  const time = formatTime(message.createdAt);

  return (
    <div className={`message-bubble ${roleClass}`}>
      <div className="message-role">
        {roleLabel[message.role] ?? "Unknown"}
        {time && <span className="message-time">{time}</span>}
      </div>
      {message.toolCalls?.length > 0 && (
        <div className="message-tool-calls">
          {message.toolCalls.map((tc) => (
            <div key={tc.toolCallId} className="tool-call-chip">
              called <code>{tc.name}</code>
            </div>
          ))}
        </div>
      )}
      {message.content &&
        // Sandbox results are shell output -- give them the bounded, scrollable monospace box rather
        // than a plain bubble that grows to the full height of a build log.
        (message.toolName === "sandbox" ? (
          <TerminalOutput text={message.content} isError={message.isError} />
        ) : (
          <div className="message-content">{message.content}</div>
        ))}
      {message.isError && <div className="message-error-flag">error</div>}
    </div>
  );
}
