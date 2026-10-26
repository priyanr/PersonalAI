"use client";

import { useState } from "react";
import { BarWidget, GaugeWidget, MarkdownWidget, StatWidget, StatusGridWidget, TableWidget } from "./SimpleWidgets";
import TimeseriesWidget from "./TimeseriesWidget";

function Body({ widget, data, asTable }) {
  if (widget.type === "markdown") return <MarkdownWidget text={widget.text} />;
  if (!data) return <p className="dash-empty">Loading…</p>;
  if (data.error) return <p className="dash-widget-error">{data.error}</p>;

  switch (widget.type) {
    case "stat":
      return <StatWidget data={data} />;
    case "gauge":
      return <GaugeWidget data={data} />;
    case "bar":
      return <BarWidget data={data} />;
    case "statusGrid":
      return <StatusGridWidget data={data} />;
    case "table":
      return <TableWidget data={data} />;
    case "timeseries":
      return <TimeseriesWidget data={data} asTable={asTable} />;
    default:
      return <p className="dash-widget-error">Unknown widget type &quot;{widget.type}&quot;.</p>;
  }
}

export default function WidgetCard({ widget, data }) {
  const [asTable, setAsTable] = useState(false);
  const canToggle = widget.type === "timeseries" && data && !data.error && data.series?.length > 0;

  return (
    <section className={`dash-widget span-${widget.span ?? 1}`} aria-label={widget.title ?? widget.id}>
      {(widget.title || canToggle) && (
        <div className="dash-widget-head">
          {widget.title ? <h3 className="dash-widget-title">{widget.title}</h3> : <span />}
          {canToggle && (
            <button type="button" className="dash-link-button" onClick={() => setAsTable((v) => !v)}>
              {asTable ? "Show chart" : "Show table"}
            </button>
          )}
        </div>
      )}
      <Body widget={widget} data={data} asTable={asTable} />
    </section>
  );
}
