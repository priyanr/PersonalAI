"use client";

import Link from "next/link";
import { useSecrets } from "@/hooks/useSecrets";
import SecretsTable from "@/components/secrets/SecretsTable";
import AddSecretForm from "@/components/secrets/AddSecretForm";
import SettingsTabs from "@/components/layout/SettingsTabs";

export default function SecretsPage() {
  const { secrets, status, error } = useSecrets();

  return (
    <main>
      <SettingsTabs />
      <h1>Secrets</h1>
      <p className="muted">
        Key/value secrets stored locally. <Link href="/settings/skills">Skills</Link> can be granted read access to
        specific keys here. Values are never shown once saved.
      </p>
      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "failed" && <p className="error-text">{error}</p>}
      <SecretsTable secrets={secrets} />
      <AddSecretForm />
    </main>
  );
}
