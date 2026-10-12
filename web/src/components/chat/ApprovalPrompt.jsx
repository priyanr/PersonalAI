"use client";

import { useState } from "react";

export default function ApprovalPrompt({ invocation, onResolve, disabled }) {
  const [busy, setBusy] = useState(false);

  async function handle(approved) {
    setBusy(true);
    try {
      await onResolve(invocation.id, approved);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="approval-prompt">
      <p>
        The assistant wants to run <code>{invocation.toolName}</code> and needs your approval:
      </p>
      <p className="approval-path">{invocation.resolvedPath}</p>
      <p className="muted">{invocation.reason}</p>
      <div className="approval-actions">
        <button disabled={busy || disabled} onClick={() => handle(true)}>
          Approve
        </button>
        <button disabled={busy || disabled} className="secondary" onClick={() => handle(false)}>
          Deny
        </button>
      </div>
    </div>
  );
}
