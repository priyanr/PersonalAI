"use client";

import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { fetchProviders } from "@/store/providersSlice";

export function useProviders() {
  const dispatch = useDispatch();
  const { items, listStatus, error } = useSelector((state) => state.providers);

  useEffect(() => {
    dispatch(fetchProviders());
  }, [dispatch]);

  return { providers: items, status: listStatus, error };
}
