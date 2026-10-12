"use client";

import { useProviders } from "@/hooks/useProviders";
import ProviderConfigForm from "@/components/providers/ProviderConfigForm";
import SettingsTabs from "@/components/layout/SettingsTabs";

export default function ProvidersPage() {
  const { providers, status, error } = useProviders();

  return (
    <main>
      <SettingsTabs />
      <h1>AI providers</h1>
      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "failed" && <p className="error-text">{error}</p>}
      <div className="provider-list">
        {providers.map((provider) => (
          <div key={provider.key} className="provider-card">
            <div className="provider-card-header">
              <h2>{provider.displayName}</h2>
              <span className={`provider-badge ${provider.isConfigured ? "provider-badge-ok" : "provider-badge-pending"}`}>
                {provider.isConfigured ? "Configured" : "Not configured"}
              </span>
            </div>
            <ProviderConfigForm provider={provider} />
          </div>
        ))}
      </div>
    </main>
  );
}
