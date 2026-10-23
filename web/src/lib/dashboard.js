// Pure helpers for dashboard widgets: number formatting, axis ticks, time labels.

const COMPACT = [
  [1e12, "T"],
  [1e9, "G"],
  [1e6, "M"],
  [1e3, "K"]
];

// 1284 -> "1.3K", 12.34 -> "12.3", 0.5 -> "0.5". Compact from 1,000 up so axis ticks and tiles stay short.
export function formatNumber(value, maxDecimals = 1) {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  const abs = Math.abs(value);
  for (const [limit, suffix] of COMPACT) {
    if (abs >= limit) return `${trim(value / limit, maxDecimals)}${suffix}`;
  }
  return trim(value, maxDecimals);
}

function trim(n, decimals) {
  return Number(n.toFixed(decimals)).toLocaleString("en-US", { maximumFractionDigits: decimals });
}

export function formatValue(value, unit) {
  if (value === null || value === undefined) return "—";
  return `${formatNumber(value)}${unit ? (unit === "%" ? "%" : ` ${unit}`) : ""}`;
}

// Round-number axis ticks covering [min, max]: 0, 25, 50, 75, 100.
export function niceTicks(min, max, count = 4) {
  if (!(max > min)) return [min];
  const rawStep = (max - min) / count;
  const magnitude = 10 ** Math.floor(Math.log10(rawStep));
  const residual = rawStep / magnitude;
  const step = (residual >= 5 ? 5 : residual >= 2 ? 2 : 1) * magnitude;
  const ticks = [];
  for (let v = Math.ceil(min / step) * step; v <= max + step * 1e-9; v += step) ticks.push(Number(v.toFixed(10)));
  return ticks;
}

export function niceCeil(value) {
  if (value <= 0) return 1;
  const ticks = niceTicks(0, value, 4);
  const top = ticks[ticks.length - 1];
  return top >= value ? top : top + (ticks[1] - ticks[0]);
}

export function formatTime(ms, spanMs) {
  const d = new Date(ms);
  const hm = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
  if (spanMs > 36 * 3600 * 1000) return d.toLocaleDateString([], { month: "short", day: "numeric" });
  if (spanMs > 20 * 3600 * 1000) return `${d.toLocaleDateString([], { weekday: "short" })} ${hm}`;
  return hm;
}

export function formatDateTime(ms) {
  return new Date(ms).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false
  });
}

export const STATUS_LABELS = { ok: "OK", warning: "Warning", critical: "Critical", none: "" };
// Status always pairs colour with an icon and a word; colour never carries the meaning alone.
export const STATUS_ICONS = { ok: "✓", warning: "!", critical: "✕", none: "•" };

export const SERIES_COUNT = 8;
