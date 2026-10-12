"use client";

import { Suspense } from "react";
import { useTools } from "@/hooks/useTools";
import ToolSettingsForm from "@/components/tools/ToolSettingsForm";
import SettingsTabs from "@/components/layout/SettingsTabs";

function ToolsList() {
  const { tools, status, error } = useTools();

  return (
    <>
      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "failed" && <p className="error-text">{error}</p>}
      {status === "succeeded" && tools.length === 0 && <p className="muted">No configurable tools yet.</p>}
      <div className="provider-list">
        {tools.map((tool) => (
          <div key={tool.name} className="provider-card">
            <div className="provider-card-header">
              <h2>{tool.displayName}</h2>
              <span className={`provider-badge ${tool.isConfigured ? "provider-badge-ok" : "provider-badge-pending"}`}>
                {tool.isConfigured ? "Configured" : "Not configured"}
              </span>
            </div>
            <ToolSettingsForm tool={tool} />
          </div>
        ))}
      </div>
    </>
  );
}

export default function ToolsSettingsPage() {
  return (
    <main>
      <SettingsTabs />
      <h1>Tools</h1>
      <p className="muted">
        Backend-defined settings for tools the agent can use. Some tools, like Gmail, require connecting an
        external account in addition to their basic configuration.
      </p>
      <Suspense fallback={<p className="muted">Loading...</p>}>
        <ToolsList />
      </Suspense>
    </main>
  );
}
