"use client";

import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { fetchSecrets } from "@/store/secretsSlice";

export function useSecrets() {
  const dispatch = useDispatch();
  const { items, listStatus, error } = useSelector((state) => state.secrets);

  useEffect(() => {
    dispatch(fetchSecrets());
  }, [dispatch]);

  return { secrets: items, status: listStatus, error };
}
