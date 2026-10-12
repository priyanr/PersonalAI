"use client";

import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { fetchTools } from "@/store/toolsSlice";

export function useTools() {
  const dispatch = useDispatch();
  const { items, listStatus, error } = useSelector((state) => state.tools);

  useEffect(() => {
    if (listStatus === "idle") dispatch(fetchTools());
  }, [dispatch, listStatus]);

  return { tools: items, status: listStatus, error };
}
