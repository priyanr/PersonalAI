"use client";

import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { fetchServerGroups, fetchServers } from "@/store/sshServersSlice";

export function useSshServers() {
  const dispatch = useDispatch();
  const { groups, servers, listStatus, error } = useSelector((state) => state.sshServers);

  useEffect(() => {
    dispatch(fetchServerGroups());
    dispatch(fetchServers());
  }, [dispatch]);

  return { groups, servers, status: listStatus, error };
}
