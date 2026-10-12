"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useSessions } from "@/hooks/useSessions";
import { SessionStatus } from "@/lib/enums";

const SEARCH_DEBOUNCE_MS = 300;

export default function SessionList() {
  const { sessions, totalCount, status, error, search, page, totalPages, setSearch, setPage } = useSessions();
  const [searchInput, setSearchInput] = useState(search);

  useEffect(() => {
    if (searchInput === search) return;
    const timer = setTimeout(() => setSearch(searchInput), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchInput]);

  // Only the very first load blanks the list; later reloads (search, paging) keep the old rows visible.
  const initialLoad = status === "loading" && sessions.length === 0 && totalCount === 0;

  return (
    <div className="session-list-panel">
      <input
        type="search"
        className="session-search"
        placeholder="Search chats by title..."
        value={searchInput}
        onChange={(e) => setSearchInput(e.target.value)}
        aria-label="Search chats"
      />

      {status === "failed" ? (
        <p className="error-text">Failed to load sessions: {error}</p>
      ) : initialLoad ? (
        <p>Loading sessions...</p>
      ) : sessions.length === 0 ? (
        <p className="muted">{search ? `No chats match "${search}".` : "No sessions yet. Create one to get started."}</p>
      ) : (
        <>
          <ul className="session-list">
            {sessions.map((s) => (
              <li key={s.id}>
                <Link href={`/sessions/${s.id}`} className="session-list-item">
                  <span className="session-title">{s.title || "Untitled chat"}</span>
                  <span className="session-meta">
                    {s.modelName} · {s.status === SessionStatus.AwaitingApproval ? "awaiting approval" : "active"}
                  </span>
                </Link>
              </li>
            ))}
          </ul>

          {totalPages > 1 && (
            <nav className="pager" aria-label="Chat pages">
              <button type="button" className="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                ← Previous
              </button>
              <span className="muted">
                Page {page} of {totalPages} · {totalCount} chats
              </span>
              <button type="button" className="secondary" disabled={page >= totalPages} onClick={() => setPage(page + 1)}>
                Next →
              </button>
            </nav>
          )}
        </>
      )}
    </div>
  );
}
