"use client";

import { useState } from "react";
import { useDispatch } from "react-redux";
import { createSecret } from "@/store/secretsSlice";

export default function AddSecretForm() {
  const dispatch = useDispatch();
  const [key, setKey] = useState("");
  const [value, setValue] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  async function handleSubmit(e) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await dispatch(createSecret({ key: key.trim(), value })).unwrap();
      setKey("");
      setValue("");
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="secret-add-form" onSubmit={handleSubmit}>
      <input
        placeholder="Key (e.g. GITHUB_TOKEN)"
        value={key}
        onChange={(e) => setKey(e.target.value)}
        required
      />
      <input
        type="password"
        placeholder="Value"
        value={value}
        onChange={(e) => setValue(e.target.value)}
        required
      />
      <button type="submit" disabled={saving}>
        {saving ? "Adding..." : "Add secret"}
      </button>
      {error && <p className="error-text">{error}</p>}
    </form>
  );
}
