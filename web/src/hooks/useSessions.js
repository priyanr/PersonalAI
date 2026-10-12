"use client";

import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { fetchSessions, sessionPageChanged, sessionSearchChanged, SESSIONS_PAGE_SIZE } from "@/store/sessionsSlice";

export function useSessions() {
  const dispatch = useDispatch();
  const { items, totalCount, listStatus, error, listQuery } = useSelector((state) => state.sessions);
  const { search, page } = listQuery;

  useEffect(() => {
    dispatch(fetchSessions({ search, page }));
  }, [dispatch, search, page]);

  const totalPages = Math.max(1, Math.ceil(totalCount / SESSIONS_PAGE_SIZE));

  // A page can go past the end if chats disappear between loads; snap back to the last real page.
  useEffect(() => {
    if (listStatus === "succeeded" && page > totalPages) dispatch(sessionPageChanged(totalPages));
  }, [dispatch, listStatus, page, totalPages]);

  return {
    sessions: items,
    totalCount,
    status: listStatus,
    error,
    search,
    page,
    totalPages,
    setSearch: (value) => dispatch(sessionSearchChanged(value)),
    setPage: (value) => dispatch(sessionPageChanged(value))
  };
}
