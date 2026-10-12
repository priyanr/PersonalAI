"use client";

import { useState } from "react";
import { useSelector } from "react-redux";
import { usePullStream } from "@/hooks/usePullStream";
import PullProgressBar from "./PullProgressBar";

export default function PullModelForm() {
  const [name, setName] = useState("");
  const { pullModel } = usePullStream();
  const pulls = useSelector((state) => state.models.pulls);

  function handleSubmit(e) {
    e.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) return;
    pullModel(trimmed);
  }

  return (
    <div className="pull-model-form">
      <form onSubmit={handleSubmit}>
        <input
          type="text"
          placeholder="Model name, e.g. llama3.1:8b"
          value={name}
          onChange={(e) => setName(e.target.value)}
        />
        <button type="submit">Pull model</button>
      </form>

      {Object.entries(pulls).map(([modelName, pull]) => (
        <div key={modelName} className="pull-model-row">
          <span className="pull-model-name">{modelName}</span>
          <PullProgressBar pull={pull} />
        </div>
      ))}
    </div>
  );
}
