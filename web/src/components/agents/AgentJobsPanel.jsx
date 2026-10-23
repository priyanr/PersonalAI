"use client";

import "../dashboard/dashboard.css";
import { useCallback, useEffect, useState } from "react";
import { apiDelete, apiGet, apiPost } from "@/lib/api";
import StatusBadge from "../dashboard/StatusBadge";

const REFRESH_MS = 15000;

function statusOf(job) {
  if (job.status === "pending_approval") return { status: "warning", label: "Needs your approval" };
  if (job.status === "disabled") return { status: "none", label: "Paused" };
  if (job.lastError) return { status: "critical", label: "Failing" };
  return { status: "ok", label: "Active" };
}

function sourceLabel(job) {
  return job.source === "llm" ? "created by the model" : "created by you";
}

function when(iso) {
  return iso ? new Date(iso).toLocaleString() : "never";
}

function JobDetails({ job }) {
  return (
    <details>
      <summary className="muted">How it parses and where it stores</summary>
      <pre>{JSON.stringify({ parser: job.parser, dataset: job.dataset, targets: job.targets ?? "every connection in scope" }, null, 2)}</pre>
    </details>
  );
}

function JobCard({ job, onAction, busy }) {
  const s = statusOf(job);
  const pending = job.status === "pending_approval";

  return (
    <div className={`job-card status-${s.status}`}>
      <div className="job-card-head">
        <strong>{job.name}</strong>
        <StatusBadge status={s.status} label={s.label} />
      </div>
      <div className="muted job-meta">
        {sourceLabel(job)} · every {job.intervalMinutes} min · {job.targets ? `on ${job.targets.join(", ")}` : "on every connection in scope"}
      </div>
      {job.description && <div className="job-meta">{job.description}</div>}
      {job.outsideAllowlist && (
        <p className="job-meta error-text">
          Not on the {job.tool} tool&apos;s allowed list. {pending ? "It will run only if you approve it, and then exactly as written below." : "It runs because you approved it as written."}{" "}
          Read it carefully: the allowed list exists to keep requests read-only.
        </p>
      )}

      {pending ? (
        <>
          <p className="job-meta">
            This exact request will be sent through the {job.tool} tool to each connection, every {job.intervalMinutes} minute(s), with no model involved.
            Nothing is collected until you approve it.
          </p>
          <pre>{job.request}</pre>
          <JobDetails job={job} />
        </>
      ) : (
        <>
          <details>
            <summary className="muted job-meta">Request</summary>
            <pre>{job.request}</pre>
          </details>
          <div className="job-meta">
            Last run: {when(job.lastRunAt)}
            {job.lastRunAt && ` · ${job.lastRowCount ?? 0} row(s)`}
          </div>
          {job.lastError && <div className="job-meta error-text">{job.lastError}</div>}
        </>
      )}

      <div className="secret-row-actions">
        {pending && (
          <button type="button" disabled={busy} onClick={() => onAction("approve", job)}>
            Approve &amp; start
          </button>
        )}
        {job.status === "active" && (
          <button type="button" className="secondary" disabled={busy} onClick={() => onAction("run", job)}>
            Run now
          </button>
        )}
        {job.status === "active" && (
          <button type="button" className="secondary" disabled={busy} onClick={() => onAction("disable", job)}>
            Pause
          </button>
        )}
        {job.status === "disabled" && (
          <button type="button" className="secondary" disabled={busy} onClick={() => onAction("enable", job)}>
            Resume
          </button>
        )}
        <button type="button" className="secondary" disabled={busy} onClick={() => onAction("delete", job)}>
          Delete
        </button>
      </div>
    </div>
  );
}

export default function AgentJobsPanel({ agentId }) {
  const [jobs, setJobs] = useState(null);
  const [datasets, setDatasets] = useState([]);
  const [busy, setBusy] = useState(false);
  const [results, setResults] = useState(null);
  const [error, setError] = useState(null);

  const load = useCallback(async () => {
    try {
      const [j, d] = await Promise.all([apiGet(`/api/agents/${agentId}/jobs`), apiGet(`/api/agents/${agentId}/datasets`)]);
      setJobs(j);
      setDatasets(d);
    } catch (err) {
      setError(err.message);
    }
  }, [agentId]);

  useEffect(() => {
    load();
    const timer = setInterval(load, REFRESH_MS);
    return () => clearInterval(timer);
  }, [load]);

  async function run(promise) {
    setBusy(true);
    setError(null);
    try {
      await promise();
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  function handleAction(action, job) {
    const base = `/api/agents/${agentId}/jobs/${job.id}`;
    if (action === "approve") {
      const ok = window.confirm(
        (job.outsideAllowlist ? `WARNING: this request is NOT on the ${job.tool} tool's allowed list.\n\n` : "") +
          `Approve "${job.name}"?\n\nThis request will be sent through the ${job.tool} tool every ${job.intervalMinutes} minute(s):\n\n${job.request}`
      );
      // The token names exactly what was shown: if the model re-saved the job meanwhile, the server refuses.
      if (ok) run(() => apiPost(`${base}/approve`, { token: job.approvalToken }));
    } else if (action === "delete") {
      if (window.confirm(`Delete job "${job.name}"? Data it already collected is kept.`)) run(() => apiDelete(base));
    } else if (action === "run") {
      run(async () => setResults(await apiPost(`${base}/run`, {})));
    } else {
      run(() => apiPost(`${base}/${action}`, {}));
    }
  }

  const pending = (jobs ?? []).filter((j) => j.status === "pending_approval");
  const others = (jobs ?? []).filter((j) => j.status !== "pending_approval");

  return (
    <section className="agent-data-panel dash">
      <h2>Collection</h2>
      <p className="muted">
        Collection jobs send requests to your data sources (servers, databases...) on their own intervals, without the model, and store the results as
        datasets that the dashboard and alert rules read. The model designs jobs when it runs; you approve them here.
      </p>

      <div className="secret-row-actions">
        <button type="button" disabled={busy || (jobs ?? []).filter((j) => j.status === "active").length === 0} onClick={() => run(async () => setResults(await apiPost(`/api/agents/${agentId}/collect-now`, {})))}>
          {busy ? "Working..." : "Run all jobs now"}
        </button>
      </div>
      {error && <p className="error-text">{error}</p>}

      {results && (
        <ul>
          {results.results.length === 0 && <li className="muted">Nothing to run: no active jobs, or no enabled connections in this agent&apos;s scope.</li>}
          {results.results.map((r, i) => (
            <li key={i} className={r.success ? "muted" : "error-text"}>
              {r.job} on {r.target}: {r.success ? `${r.rows} row(s)` : r.error}
            </li>
          ))}
        </ul>
      )}

      {pending.length > 0 && (
        <>
          <h3>Waiting for your approval ({pending.length})</h3>
          {pending.map((job) => (
            <JobCard key={job.id} job={job} busy={busy} onAction={handleAction} />
          ))}
        </>
      )}

      {jobs && jobs.length === 0 && (
        <p className="muted">
          No jobs yet. Run the agent and describe in its prompt what to monitor: it will explore your data sources and propose jobs
          for you to approve.
        </p>
      )}
      {others.map((job) => (
        <JobCard key={job.id} job={job} busy={busy} onAction={handleAction} />
      ))}

      {datasets.length > 0 && (
        <>
          <h3>Collected data</h3>
          <table className="secrets-table">
            <thead>
              <tr>
                <th>Dataset</th>
                <th>Kind</th>
                <th>Points</th>
                <th>Last update</th>
              </tr>
            </thead>
            <tbody>
              {datasets.map((d) => (
                <tr key={d.name} title={d.schemaHint ?? ""}>
                  <td>{d.name}</td>
                  <td className="muted">{d.kind}</td>
                  <td className="muted">{d.pointCount}</td>
                  <td className="muted">{d.lastTs ? new Date(d.lastTs).toLocaleString() : "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}
