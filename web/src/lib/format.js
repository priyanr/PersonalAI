// Compact token count for status lines: 950, 12.3k, 1.2M.
export function formatTokens(n) {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return String(n);
}

// Hover text for a compact token count: the exact input/output split.
export function tokenBreakdown(inputTokens, outputTokens) {
  return `In: ${inputTokens.toLocaleString()} · Out: ${outputTokens.toLocaleString()}`;
}
