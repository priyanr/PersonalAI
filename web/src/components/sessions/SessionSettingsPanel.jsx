"use client";

import { useMemo, useState } from "react";
import { useDispatch } from "react-redux";
import { updateSessionSettings } from "@/store/sessionsSlice";
import { useSkills } from "@/hooks/useSkills";
import { sessionToOptions, optionsToPayload } from "@/lib/chatOptions";
import ChatOptionsSummary from "@/components/sessions/ChatOptionsSummary";
import ChatOptionsSheet from "@/components/sessions/ChatOptionsSheet";

export default function SessionSettingsPanel({ session }) {
  const dispatch = useDispatch();
  const { skills } = useSkills();
  const [open, setOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  const options = useMemo(() => sessionToOptions(session), [session]);

  async function handleSave(draft) {
    setSaving(true);
    setError(null);
    try {
      const { providerKey, modelName, ...settings } = optionsToPayload(draft);
      const modelChanged = providerKey !== options.providerKey || modelName !== options.modelName;
      await dispatch(
        updateSessionSettings({
          sessionId: session.id,
          ...settings,
          ...(modelChanged ? { providerKey, modelName } : {})
        })
      ).unwrap();
      setOpen(false);
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  function handleClose() {
    setOpen(false);
    setError(null);
  }

  return (
    <div className="session-settings-panel">
      <ChatOptionsSummary options={options} skills={skills} onEdit={() => setOpen(true)} />
      {open && (
        <ChatOptionsSheet
          initial={options}
          skills={skills}
          onSubmit={handleSave}
          onClose={handleClose}
          submitLabel="Save changes"
          submitting={saving}
          error={error}
        />
      )}
    </div>
  );
}
