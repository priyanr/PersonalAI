// Shell output needs a <pre>: .tool-activity-item and .message-content both collapse or reflow long
// command output, and the activity feed's 200-character truncation cuts off exactly the tail where a
// stack trace or error message lives. This renders the sandbox tool's result verbatim in a bounded,
// scrollable box instead -- the box scrolls rather than letting one `pip install` make the transcript
// thousands of pixels tall.
export default function TerminalOutput({ text, isError = false }) {
  if (!text) return null;

  return (
    <pre className={`terminal-output ${isError ? "is-error" : ""}`}>
      {text}
    </pre>
  );
}
