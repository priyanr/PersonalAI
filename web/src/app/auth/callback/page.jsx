"use client";

import { Suspense, useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { completeLogin } from "@/lib/auth";

function CallbackHandler() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [error, setError] = useState(null);

  useEffect(() => {
    // RFC 6749 §4.1.2.1: a denied/failed authorization comes back as ?error=...&error_description=...
    const providerError = searchParams.get("error");
    if (providerError) {
      setError(searchParams.get("error_description") || providerError);
      return;
    }

    const code = searchParams.get("code");
    const state = searchParams.get("state");
    completeLogin(code, state)
      .then((returnTo) => router.replace(returnTo || "/sessions"))
      .catch((err) => setError(err.message));
  }, [searchParams, router]);

  if (error) return <p>Login failed: {error}</p>;
  return <p>Signing you in…</p>;
}

export default function AuthCallbackPage() {
  return (
    <main className="callback-main">
      <Suspense fallback={<p>Signing you in…</p>}>
        <CallbackHandler />
      </Suspense>
    </main>
  );
}
