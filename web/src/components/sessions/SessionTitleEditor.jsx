"use client";

import { useState } from "react";
import { useDispatch } from "react-redux";
import { renameSession } from "@/store/sessionsSlice";

const MAX_TITLE_LENGTH = 200; // mirrors SessionsController.MaxTitleLength

export default function SessionTitleEditor({ session }) {
  const dispatch = useDispatch();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  const startEditing = () => {
    setDraft(session.title || "");
    setError(null);
    setEditing(true);
  };

  const cancel = () => {
    setEditing(false);
    setError(null);
  };

  const save = async () => {
    const title = draft.trim();
    if (!title) {
      setError("Title can't be empty.");
      return;
    }
    if (title === session.title) {
      setEditing(false);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      await dispatch(renameSession({ sessionId: session.id, title })).unwrap();
      setEditing(false);
    } catch (e) {
      setError(e.message || "Rename failed.");
    } finally {
      setSaving(false);
    }
  };

  if (!editing) {
    return (
      <div className="session-title-row">
        <h1>{session.title || "Untitled chat"}</h1>
        <button type="button" className="link-button" onClick={startEditing}>
          Rename
        </button>
      </div>
    );
  }

  return (
    <form
      className="session-title-row"
      onSubmit={(e) => {
        e.preventDefault();
        save();
      }}
    >
      <input
        type="text"
        className="session-title-input"
        value={draft}
        maxLength={MAX_TITLE_LENGTH}
        autoFocus
        disabled={saving}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape") cancel();
        }}
        aria-label="Chat title"
      />
      <button type="submit" disabled={saving}>
        {saving ? "Saving..." : "Save"}
      </button>
      <button type="button" className="secondary" disabled={saving} onClick={cancel}>
        Cancel
      </button>
      {error && <span className="error-text">{error}</span>}
    </form>
  );
}
