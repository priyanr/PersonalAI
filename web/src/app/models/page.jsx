"use client";

import { useState } from "react";
import ModelTable from "@/components/models/ModelTable";
import PullModelForm from "@/components/models/PullModelForm";
import { useProviders } from "@/hooks/useProviders";

export default function ModelsPage() {
  const { providers } = useProviders();
  const [providerKey, setProviderKey] = useState("ollama");

  return (
    <main>
      <div className="top-nav">
        <h1>Models</h1>
      </div>
      {providers.length > 1 && (
        <label className="settings-number provider-select">
          Provider
          <select value={providerKey} onChange={(e) => setProviderKey(e.target.value)}>
            {providers.map((p) => (
              <option key={p.key} value={p.key}>
                {p.displayName}
              </option>
            ))}
          </select>
        </label>
      )}
      {providerKey === "ollama" && <PullModelForm />}
      <ModelTable providerKey={providerKey} />
    </main>
  );
}
