"use client";

import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { fetchAgents } from "@/store/agentsSlice";

export function useAgents() {
  const dispatch = useDispatch();
  const { items, listStatus, error } = useSelector((state) => state.agents);

  useEffect(() => {
    dispatch(fetchAgents());
  }, [dispatch]);

  return { agents: items, status: listStatus, error };
}
