import TerminalOutput from "./TerminalOutput";

export default function ToolCallActivity({ events }) {
  if (!events || events.length === 0) return null;

  return (
    <div className="tool-activity">
      {events.map((e, i) =>
        e.type === "tool-result" ? (
          // Sandbox results are shell output: rendering them through truncate() would collapse the
          // newlines and cut off the tail, which is exactly where the error message is.
          e.toolName === "sandbox" ? (
            <div key={i} className="tool-activity-sandbox">
              <div className={`tool-activity-item ${e.isError ? "is-error" : ""}`}>
                <code>{e.toolName}</code>
              </div>
              <TerminalOutput text={e.resultContent || "(no output)"} isError={e.isError} />
            </div>
          ) : (
            <div key={i} className={`tool-activity-item ${e.isError ? "is-error" : ""}`}>
              <code>{e.toolName}</code> → {truncate(e.resultContent)}
            </div>
          )
        ) : e.type === "tool-progress" ? (
          <div key={i} className="tool-activity-item" style={{ color: "var(--text-muted)", fontStyle: "italic" }}>
            <code>{e.toolName}</code> {e.message}
          </div>
        ) : (
          <div key={i} className="tool-activity-item is-pending">
            <code>{e.toolName}</code> needs approval: {e.reason}
          </div>
        )
      )}
    </div>
  );
}

function truncate(text) {
  if (!text) return "(no output)";
  return text.length > 200 ? text.slice(0, 200) + "..." : text;
}
