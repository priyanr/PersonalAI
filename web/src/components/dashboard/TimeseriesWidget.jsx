"use client";

import { useCallback, useMemo, useRef, useState } from "react";
import { formatDateTime, formatNumber, formatTime, formatValue, niceCeil, niceTicks } from "@/lib/dashboard";

const HEIGHT = 220;
const MARGIN = { left: 44, right: 52, top: 10, bottom: 24 };
const END_LABEL_MAX_SERIES = 4;

// A callback ref, not an effect on a ref object: the chart element only exists once there is data (the widget returns
// early for an empty window), so an effect that ran once on mount would find nothing and never measure it. This attaches
// whenever the element mounts and detaches when it goes away.
function useElementWidth() {
  const observerRef = useRef(null);
  const [width, setWidth] = useState(600);
  const ref = useCallback((el) => {
    observerRef.current?.disconnect();
    observerRef.current = null;
    if (!el) return;
    const measure = () => setWidth(Math.max(180, Math.round(el.getBoundingClientRect().width)));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    observerRef.current = observer;
  }, []);
  return [ref, width];
}

const seriesColor = (i) => `var(--series-${(i % 8) + 1})`;

export default function TimeseriesWidget({ data, asTable }) {
  const [ref, width] = useElementWidth();
  const [hoverTime, setHoverTime] = useState(null);

  const { times, xMin, xMax, yMin, yMax, valueAt } = useMemo(() => {
    const all = new Set();
    let lo = Infinity;
    let hi = -Infinity;
    const lookup = data.series.map((s) => new Map(s.points.map(([t, v]) => [t, v])));
    data.series.forEach((s) =>
      s.points.forEach(([t, v]) => {
        all.add(t);
        lo = Math.min(lo, v);
        hi = Math.max(hi, v);
      })
    );
    const sorted = [...all].sort((a, b) => a - b);
    const start = sorted[0] ?? 0;
    const end = sorted[sorted.length - 1] ?? 1;
    const top = Math.max(hi, data.crit ?? -Infinity, data.warn ?? -Infinity);
    return {
      times: sorted,
      xMin: start,
      xMax: end === start ? start + 60000 : end,
      yMin: data.min ?? Math.min(0, lo === Infinity ? 0 : lo),
      yMax: data.max ?? niceCeil(top === -Infinity ? 1 : top),
      valueAt: (seriesIndex, t) => lookup[seriesIndex].get(t) ?? null
    };
  }, [data]);

  if (data.series.length === 0) return <p className="dash-empty">No data in this window</p>;
  if (asTable) return <TimeseriesTable data={data} times={times} />;

  const plotW = width - MARGIN.left - MARGIN.right;
  const plotH = HEIGHT - MARGIN.top - MARGIN.bottom;
  const x = (t) => MARGIN.left + ((t - xMin) / (xMax - xMin)) * plotW;
  const y = (v) => MARGIN.top + (1 - (v - yMin) / (yMax - yMin || 1)) * plotH;
  const yTicks = niceTicks(yMin, yMax, 4);
  const xTickCount = Math.max(2, Math.min(5, Math.floor(plotW / 110)));
  const xTicks = Array.from({ length: xTickCount }, (_, i) => xMin + ((xMax - xMin) * i) / (xTickCount - 1));
  const span = xMax - xMin;

  function nearestTime(clientX, rect) {
    const t = xMin + ((clientX - rect.left - MARGIN.left) / plotW) * span;
    let best = times[0];
    for (const candidate of times) if (Math.abs(candidate - t) < Math.abs(best - t)) best = candidate;
    return best;
  }

  function onKeyDown(e) {
    if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
    e.preventDefault();
    const current = hoverTime === null ? times.length : times.indexOf(hoverTime);
    const next = Math.min(times.length - 1, Math.max(0, current + (e.key === "ArrowRight" ? 1 : -1)));
    setHoverTime(times[next]);
  }

  const thresholds = [
    data.warn !== null && data.warn !== undefined && { value: data.warn, name: "warn" },
    data.crit !== null && data.crit !== undefined && { value: data.crit, name: "crit" }
  ].filter(Boolean);

  // End-of-line values only where they don't collide; otherwise the legend + tooltip carry them.
  const ends = data.series.map((s, i) => ({ i, v: s.points[s.points.length - 1][1] }));
  const labelEnds =
    data.series.length <= END_LABEL_MAX_SERIES &&
    [...ends].sort((a, b) => y(a.v) - y(b.v)).every((e, idx, arr) => idx === 0 || y(e.v) - y(arr[idx - 1].v) >= 13);

  const tooltipLeft = hoverTime === null ? 0 : x(hoverTime) > width / 2 ? Math.max(0, x(hoverTime) - 196) : x(hoverTime) + 12;

  return (
    <div className="dash-chart" ref={ref}>
      <svg
        width={width}
        height={HEIGHT}
        viewBox={`0 0 ${width} ${HEIGHT}`}
        role="img"
        tabIndex={0}
        aria-label={`Line chart, ${data.series.length} series. Use left and right arrow keys to read values.`}
        onPointerMove={(e) => setHoverTime(nearestTime(e.clientX, e.currentTarget.getBoundingClientRect()))}
        onPointerLeave={() => setHoverTime(null)}
        onKeyDown={onKeyDown}
        onBlur={() => setHoverTime(null)}
      >
        {yTicks.map((v) => (
          <g key={v}>
            <line x1={MARGIN.left} x2={width - MARGIN.right} y1={y(v)} y2={y(v)} stroke="var(--viz-grid)" strokeWidth="1" />
            <text className="tick-label-value" x={MARGIN.left - 8} y={y(v) + 4} textAnchor="end">
              {formatNumber(v)}
            </text>
          </g>
        ))}
        <line x1={MARGIN.left} x2={width - MARGIN.right} y1={y(yMin)} y2={y(yMin)} stroke="var(--viz-axis)" strokeWidth="1" />
        {xTicks.map((t, i) => (
          <text key={i} x={x(t)} y={HEIGHT - 6} textAnchor={i === 0 ? "start" : i === xTicks.length - 1 ? "end" : "middle"}>
            {formatTime(t, span)}
          </text>
        ))}

        {thresholds.map((t) => (
          <g key={t.name}>
            <line
              x1={MARGIN.left}
              x2={width - MARGIN.right}
              y1={y(t.value)}
              y2={y(t.value)}
              stroke={t.name === "crit" ? "var(--status-critical)" : "var(--status-warning)"}
              strokeWidth="1"
              opacity="0.7"
            />
            <text x={width - MARGIN.right + 4} y={y(t.value) + 4}>
              {t.name} {formatNumber(t.value)}
            </text>
          </g>
        ))}

        {data.series.length === 1 && (
          <path
            d={`M${x(data.series[0].points[0][0])},${y(yMin)} ${data.series[0].points.map(([t, v]) => `L${x(t)},${y(v)}`).join(" ")} L${x(
              data.series[0].points[data.series[0].points.length - 1][0]
            )},${y(yMin)} Z`}
            fill={seriesColor(0)}
            opacity="0.1"
          />
        )}
        {data.series.map((s, i) => (
          <path
            key={s.label}
            d={s.points.map(([t, v], k) => `${k === 0 ? "M" : "L"}${x(t)},${y(v)}`).join(" ")}
            fill="none"
            stroke={seriesColor(i)}
            strokeWidth="2"
            strokeLinejoin="round"
            strokeLinecap="round"
          />
        ))}

        {data.series.map((s, i) => {
          const [t, v] = s.points[s.points.length - 1];
          return (
            <g key={s.label}>
              <circle cx={x(t)} cy={y(v)} r="4" fill={seriesColor(i)} stroke="var(--panel)" strokeWidth="2" />
              {labelEnds && thresholds.every((th) => Math.abs(y(v) - y(th.value)) >= 12) && (
                <text className="end-label" x={x(t) + 9} y={y(v) + 4}>
                  {formatNumber(v)}
                </text>
              )}
            </g>
          );
        })}

        {hoverTime !== null && (
          <g>
            <line x1={x(hoverTime)} x2={x(hoverTime)} y1={MARGIN.top} y2={y(yMin)} stroke="var(--viz-axis)" strokeWidth="1" />
            {data.series.map((s, i) => {
              const v = valueAt(i, hoverTime);
              return v === null ? null : (
                <circle key={s.label} cx={x(hoverTime)} cy={y(v)} r="4" fill={seriesColor(i)} stroke="var(--panel)" strokeWidth="2" />
              );
            })}
          </g>
        )}
      </svg>

      {hoverTime !== null && (
        <div className="dash-tooltip" style={{ left: tooltipLeft }}>
          <div className="dash-tooltip-time">{formatDateTime(hoverTime)}</div>
          {data.series.map((s, i) => (
            <div className="dash-tooltip-row" key={s.label}>
              <span className="dash-legend-key" style={{ "--key-color": seriesColor(i) }} />
              <span className="name">{s.label}</span>
              <strong>{formatValue(valueAt(i, hoverTime), data.unit)}</strong>
            </div>
          ))}
        </div>
      )}

      {data.series.length > 1 && (
        <div className="dash-legend">
          {data.series.map((s, i) => (
            <span className="dash-legend-item" key={s.label}>
              <span className="dash-legend-key" style={{ "--key-color": seriesColor(i) }} />
              {s.label}
            </span>
          ))}
        </div>
      )}
      {data.hiddenSeries > 0 && (
        <p className="dash-hidden-note">
          +{data.hiddenSeries} more series not shown (the {data.lowerIsWorse ? "lowest" : "highest"} {data.series.length} are plotted)
        </p>
      )}
    </div>
  );
}

// The table view: every plotted value, newest first, reachable without hovering.
function TimeseriesTable({ data, times }) {
  const rows = [...times].reverse().slice(0, 60);
  const lookup = data.series.map((s) => new Map(s.points.map(([t, v]) => [t, v])));
  return (
    <div className="dash-table-wrap">
      <table className="dash-table">
        <thead>
          <tr>
            <th>Time</th>
            {data.series.map((s) => (
              <th key={s.label} className="num">
                {s.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((t) => (
            <tr key={t}>
              <td>{formatDateTime(t)}</td>
              {lookup.map((m, i) => (
                <td key={i} className="num">
                  {formatValue(m.get(t) ?? null, data.unit)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
