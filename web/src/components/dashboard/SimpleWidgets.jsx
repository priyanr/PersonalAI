import { formatNumber, formatValue } from "@/lib/dashboard";
import StatusBadge from "./StatusBadge";

export function StatWidget({ data }) {
  if (data.value === null || data.value === undefined) return <p className="dash-empty">{data.message ?? "No data"}</p>;
  return (
    <div>
      <div>
        <span className="dash-stat-value">{formatNumber(data.value)}</span>
        {data.unit && <span className="dash-stat-unit">{data.unit}</span>}
      </div>
      {data.status && <StatusBadge status={data.status} />}
      {data.detail && <div className="dash-stat-detail">{data.detail}</div>}
    </div>
  );
}

// A single value against a range: the fill carries severity, the track is a quiet step of the surface.
export function GaugeWidget({ data }) {
  if (data.value === null || data.value === undefined) return <p className="dash-empty">{data.message ?? "No data"}</p>;
  const min = data.min ?? 0;
  const max = data.max ?? 100;
  const pct = max > min ? Math.min(100, Math.max(0, ((data.value - min) / (max - min)) * 100)) : 0;
  return (
    <div>
      <div>
        <span className="dash-stat-value">{formatNumber(data.value)}</span>
        {data.unit && <span className="dash-stat-unit">{data.unit}</span>}
      </div>
      <div
        className="dash-meter-track"
        role="meter"
        aria-valuemin={min}
        aria-valuemax={max}
        aria-valuenow={data.value}
      >
        <div className={`dash-meter-fill status-${data.status ?? "none"}`} style={{ width: `${pct}%` }} />
      </div>
      <div className="dash-meter-scale">
        <span>{formatNumber(min)}</span>
        <span>{formatValue(max, data.unit)}</span>
      </div>
      {data.status && <StatusBadge status={data.status} />}
      {data.detail && <div className="dash-stat-detail">{data.detail}</div>}
    </div>
  );
}

export function BarWidget({ data }) {
  if (data.items.length === 0) return <p className="dash-empty">No data</p>;
  const max = data.max ?? Math.max(...data.items.map((i) => i.value), 1);
  return (
    <>
      <div className="dash-bars">
        {data.items.map((item) => (
          <div className="dash-bar-row" key={item.label} title={`${item.label}: ${formatValue(item.value, data.unit)}`}>
            <span className="dash-bar-label">{item.label}</span>
            <span className="dash-bar-value">
              {item.status && item.status !== "ok" && <StatusBadge status={item.status} hideWord />}
              {formatValue(item.value, data.unit)}
            </span>
            <div className="dash-bar-track">
              <div
                className={`dash-bar-fill status-${item.status ?? "none"}`}
                style={{ width: `${Math.min(100, Math.max(0, (item.value / max) * 100))}%` }}
              />
            </div>
          </div>
        ))}
      </div>
      {data.hidden > 0 && <p className="dash-hidden-note">+{data.hidden} more not shown</p>}
    </>
  );
}

export function StatusGridWidget({ data }) {
  if (data.items.length === 0) return <p className="dash-empty">No data</p>;
  return (
    <>
      <div className="dash-tiles">
        {data.items.map((item) => (
          <div className={`dash-tile status-${item.status}`} key={item.label}>
            <div className="dash-tile-label" title={item.label}>
              {item.label}
            </div>
            <div className="dash-tile-state">
              <StatusBadge status={item.status} label={item.text} />
            </div>
          </div>
        ))}
      </div>
      {data.hidden > 0 && <p className="dash-hidden-note">+{data.hidden} more not shown</p>}
    </>
  );
}

function cell(value) {
  if (value === null || value === undefined) return "—";
  if (typeof value === "boolean") return value ? "yes" : "no";
  if (typeof value === "number") return formatNumber(value, 2);
  return String(value);
}

export function TableWidget({ data }) {
  if (data.rows.length === 0) return <p className="dash-empty">No data</p>;
  const numeric = data.columns.map((_, i) => data.rows.every((r) => typeof r[i] === "number" || r[i] == null));
  return (
    <>
      <div className="dash-table-wrap">
        <table className="dash-table">
          <thead>
            <tr>
              {data.columns.map((c, i) => (
                <th key={c} className={numeric[i] ? "num" : undefined}>
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {data.rows.map((row, r) => (
              <tr key={r}>
                {row.map((v, i) => (
                  <td key={i} className={numeric[i] ? "num" : undefined}>
                    {cell(v)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {data.hidden > 0 && <p className="dash-hidden-note">+{data.hidden} more rows not shown</p>}
    </>
  );
}

// Minimal, safe markdown: headings, bullet lists, paragraphs, **bold** and `code`. Built from React
// elements only (model-written text is untrusted, so there is no innerHTML anywhere).
function inline(text, keyPrefix) {
  return text.split(/(\*\*[^*]+\*\*|`[^`]+`)/g).map((part, i) => {
    if (part.startsWith("**") && part.endsWith("**") && part.length > 4) return <strong key={`${keyPrefix}${i}`}>{part.slice(2, -2)}</strong>;
    if (part.startsWith("`") && part.endsWith("`") && part.length > 2) return <code key={`${keyPrefix}${i}`}>{part.slice(1, -1)}</code>;
    return part;
  });
}

export function MarkdownWidget({ text }) {
  const blocks = [];
  let list = null;
  (text ?? "").split("\n").forEach((raw, i) => {
    const line = raw.trimEnd();
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line);
    if (bullet) {
      if (!list) blocks.push((list = { type: "ul", items: [] }));
      list.items.push(bullet[1]);
      return;
    }
    list = null;
    if (!line.trim()) return;
    const heading = /^(#{1,3})\s+(.*)$/.exec(line);
    blocks.push(heading ? { type: heading[1].length === 1 ? "h3" : "h4", text: heading[2] } : { type: "p", text: line });
  });

  return (
    <div className="dash-markdown">
      {blocks.map((b, i) => {
        if (b.type === "ul")
          return (
            <ul key={i}>
              {b.items.map((item, j) => (
                <li key={j}>{inline(item, `${i}-${j}-`)}</li>
              ))}
            </ul>
          );
        const Tag = b.type;
        return <Tag key={i}>{inline(b.text, `${i}-`)}</Tag>;
      })}
    </div>
  );
}
