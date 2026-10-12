"use client";

import { useEffect, useId, useState } from "react";

/**
 * Searchable model picker: a text input backed by a <datalist>, so the browser filters the model
 * list as you type. The parent's value only changes when the text exactly matches a model name, so
 * partial typing never wipes a saved model; on blur, unmatched text reverts to the last valid value.
 * Give it a `key` that changes with the provider so the text resets when the model list is swapped.
 */
export default function ModelSelect({ models, value, onChange, loading = false, required = false }) {
  const listId = useId();
  const [text, setText] = useState(value);
  const isModel = (name) => models.some((m) => m.name === name);

  // Parent changed the value (e.g. loaded an existing record) — show it.
  useEffect(() => {
    setText(value);
  }, [value]);

  // Text typed before the list finished loading becomes valid once the list arrives.
  useEffect(() => {
    if (text && text !== value && isModel(text)) onChange(text);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [models]);

  function handleChange(e) {
    const next = e.target.value;
    setText(next);
    if (isModel(next)) onChange(next);
  }

  function handleBlur() {
    // While loading we can't tell a typo from a not-yet-loaded model, so leave the text alone.
    if (!loading && !isModel(text)) setText(value);
  }

  return (
    <>
      <input
        type="text"
        list={listId}
        value={text}
        onChange={handleChange}
        onBlur={handleBlur}
        onFocus={(e) => e.target.select()}
        placeholder={loading ? "Loading models..." : "Search models..."}
        autoComplete="off"
        required={required}
        ref={(el) => el?.setCustomValidity(text && text !== value ? "Choose a model from the list." : "")}
      />
      <datalist id={listId}>
        {models.map((m) => (
          <option key={m.name} value={m.name} />
        ))}
      </datalist>
    </>
  );
}
