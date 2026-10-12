"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useDispatch } from "react-redux";
import { useModels } from "@/hooks/useModels";
import ModelSelect from "@/components/models/ModelSelect";
import { useProviders } from "@/hooks/useProviders";
import { useSkills } from "@/hooks/useSkills";
import { createSession } from "@/store/sessionsSlice";
import { defaultOptions, optionsToPayload } from "@/lib/chatOptions";
import ChatOptionsSummary from "@/components/sessions/ChatOptionsSummary";
import ChatOptionsSheet from "@/components/sessions/ChatOptionsSheet";

export default function NewSessionDialog() {
  const dispatch = useDispatch();
  const router = useRouter();
  const { providers } = useProviders();
  const { skills } = useSkills();
  const [providerKey, setProviderKey] = useState("ollama");
  const { models, status } = useModels(providerKey);
  const [title, setTitle] = useState("");
  const [modelName, setModelName] = useState("");

  // Model list is provider-scoped — a model chosen under one provider isn't valid under another.
  useEffect(() => {
    setModelName("");
  }, [providerKey]);
  const [options, setOptions] = useState(defaultOptions);
  const [optionsOpen, setOptionsOpen] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);

  function handleApplyOptions(draft) {
    setOptions(draft);
    setOptionsOpen(false);
  }

  async function handleSubmit(e) {
    e.preventDefault();
    if (!modelName) {
      setError("Choose a model first.");
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      const session = await dispatch(
        createSession({
          title: title.trim() || null,
          modelName,
          providerKey,
          ...optionsToPayload(options)
        })
      ).unwrap();
      router.push(`/sessions/${session.id}`);
    } catch (err) {
      setError(err.message);
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <>
      <form className="new-session-form" onSubmit={handleSubmit}>
        <input
          type="text"
          className="new-session-title"
          placeholder="Chat title (optional)"
          aria-label="Chat title"
          value={title}
          onChange={(e) => setTitle(e.target.value)}
        />
        <div className="new-session-model">
          {providers.length > 1 && (
            <select value={providerKey} onChange={(e) => setProviderKey(e.target.value)} aria-label="Provider">
              {providers.map((p) => (
                <option key={p.key} value={p.key}>
                  {p.displayName}
                </option>
              ))}
            </select>
          )}
          <ModelSelect
            key={providerKey}
            models={models}
            value={modelName}
            onChange={setModelName}
            loading={status === "loading"}
          />
        </div>
        <div className="new-session-footer">
          <ChatOptionsSummary options={options} skills={skills} onEdit={() => setOptionsOpen(true)} />
          <button type="submit" disabled={submitting}>
            {submitting ? "Starting..." : "Start chat"}
          </button>
        </div>
        {models.length === 0 && status === "succeeded" && (
          <p className="muted">
            {providerKey === "ollama"
              ? "No models installed yet. Pull one from the Models page."
              : "No models available. Check this provider's configuration on the AI providers page."}
          </p>
        )}
        {error && <p className="error-text">{error}</p>}
      </form>
      {optionsOpen && (
        <ChatOptionsSheet
          initial={options}
          skills={skills}
          onSubmit={handleApplyOptions}
          onClose={() => setOptionsOpen(false)}
          submitLabel="Apply"
        />
      )}
    </>
  );
}
