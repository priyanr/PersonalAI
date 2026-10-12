"use client";

import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { fetchModels } from "@/store/modelsSlice";

export function useModels(providerKey = "ollama") {
  const dispatch = useDispatch();
  const { items, listStatus, error, pulls } = useSelector((state) => state.models);

  useEffect(() => {
    dispatch(fetchModels(providerKey));
  }, [dispatch, providerKey]);

  return { models: items, status: listStatus, error, pulls };
}
