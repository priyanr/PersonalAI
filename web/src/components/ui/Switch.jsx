"use client";

import { useId } from "react";

/**
 * On/off switch. With a `label` it renders as a full-width, fully clickable row: label and optional
 * description on the left, the track on the right. Without one, pass `ariaLabel`.
 */
export default function Switch({ checked, onChange, label, description, ariaLabel, disabled = false, className = "" }) {
  const labelId = useId();
  const descId = useId();

  const track = <span className="switch-track" aria-hidden="true"><span className="switch-thumb" /></span>;

  if (!label) {
    return (
      <button
        type="button"
        role="switch"
        className={`switch ${className}`}
        aria-checked={checked}
        aria-label={ariaLabel}
        disabled={disabled}
        onClick={() => onChange(!checked)}
      >
        {track}
      </button>
    );
  }

  return (
    <button
      type="button"
      role="switch"
      className={`switch opt-row ${className}`}
      aria-checked={checked}
      aria-labelledby={labelId}
      aria-describedby={description ? descId : undefined}
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span className="opt-row-text">
        <span id={labelId} className="opt-row-label">{label}</span>
        {description && <span id={descId} className="opt-row-desc">{description}</span>}
      </span>
      {track}
    </button>
  );
}
