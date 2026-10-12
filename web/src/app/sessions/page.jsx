"use client";

import SessionList from "@/components/sessions/SessionList";
import NewSessionDialog from "@/components/sessions/NewSessionDialog";

export default function SessionsPage() {
  return (
    <main>
      <div className="top-nav">
        <h1>Chats</h1>
      </div>
      <NewSessionDialog />
      <SessionList />
    </main>
  );
}
