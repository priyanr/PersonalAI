import { STATUS_ICONS, STATUS_LABELS } from "@/lib/dashboard";

// Status is always icon + word; the colour only reinforces it.
export default function StatusBadge({ status, label, hideWord = false }) {
  const s = status ?? "none";
  return (
    <span className={`dash-status status-${s}`}>
      <span className="dash-status-icon" aria-hidden="true">
        {STATUS_ICONS[s]}
      </span>
      {!hideWord && (label ?? STATUS_LABELS[s]) ? <span>{label ?? STATUS_LABELS[s]}</span> : <span className="sr-only">{STATUS_LABELS[s]}</span>}
    </span>
  );
}
