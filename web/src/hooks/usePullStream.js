"use client";

import { useDispatch } from "react-redux";
import { streamSse } from "@/lib/sse";
import { pullStarted, pullProgress, pullSucceeded, pullFailed, fetchModels } from "@/store/modelsSlice";

export function usePullStream() {
  const dispatch = useDispatch();

  async function pullModel(name) {
    dispatch(pullStarted(name));
    try {
      await streamSse("/api/models/pull", { name }, (event) => {
        if (event.type === "error") {
          dispatch(pullFailed({ name, error: event.message }));
        } else if (event.status) {
          dispatch(pullProgress({ name, status: event.status, total: event.total, completed: event.completed }));
          if (event.status === "success") {
            dispatch(pullSucceeded(name));
          }
        }
      });
    } catch (err) {
      dispatch(pullFailed({ name, error: err.message }));
    } finally {
      dispatch(fetchModels());
    }
  }

  return { pullModel };
}
