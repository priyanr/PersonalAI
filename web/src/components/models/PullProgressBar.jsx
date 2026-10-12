export default function PullProgressBar({ pull }) {
  if (!pull) return null;

  const pct =
    pull.total && pull.completed ? Math.min(100, Math.round((pull.completed / pull.total) * 100)) : null;

  return (
    <div className="pull-progress">
      <div className="pull-progress-status">
        {pull.status === "error" ? `Error: ${pull.error}` : pull.statusText}
        {pct !== null && ` (${pct}%)`}
      </div>
      {pull.status === "pulling" && (
        <div className="pull-progress-track">
          <div
            className="pull-progress-fill"
            style={{ width: pct !== null ? `${pct}%` : "100%", opacity: pct !== null ? 1 : 0.4 }}
          />
        </div>
      )}
    </div>
  );
}
