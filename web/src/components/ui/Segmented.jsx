"use client";

import { useRef } from "react";

/**
 * Single-choice segmented control (a radiogroup of buttons). Arrow keys move and select, like native
 * radios; only the selected segment is in the tab order. Options are `{ value, label, short? }` —
 * `short` is shown when present, with the full label as its tooltip.
 */
export default function Segmented({ options, value, onChange, ariaLabel, ariaLabelledBy, disabled = false }) {
  const refs = useRef([]);
  // A value matching no option (e.g. a custom mix) selects nothing; the first segment then takes the tab stop.
  const selectedIndex = options.findIndex((o) => o.value === value);

  function handleKeyDown(e, index) {
    const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[e.key];
    let next = step ? (index + step + options.length) % options.length : null;
    if (e.key === "Home") next = 0;
    if (e.key === "End") next = options.length - 1;
    if (next === null) return;
    e.preventDefault();
    onChange(options[next].value);
    refs.current[next]?.focus();
  }

  return (
    <div className="segmented" role="radiogroup" aria-label={ariaLabel} aria-labelledby={ariaLabelledBy}>
      {options.map((opt, i) => {
        const selected = i === selectedIndex;
        return (
          <button
            key={opt.value}
            ref={(el) => (refs.current[i] = el)}
            type="button"
            role="radio"
            aria-checked={selected}
            tabIndex={selected || (selectedIndex === -1 && i === 0) ? 0 : -1}
            disabled={disabled}
            title={opt.short ? opt.label : undefined}
            onClick={() => onChange(opt.value)}
            onKeyDown={(e) => handleKeyDown(e, i)}
          >
            {opt.short ?? opt.label}
          </button>
        );
      })}
    </div>
  );
}
