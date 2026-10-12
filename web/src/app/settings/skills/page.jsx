"use client";

import Link from "next/link";
import { useSkills } from "@/hooks/useSkills";
import SkillList from "@/components/skills/SkillList";
import SettingsTabs from "@/components/layout/SettingsTabs";

export default function SkillsPage() {
  const { skills, status, error } = useSkills();

  return (
    <main>
      <SettingsTabs />
      <div className="top-nav">
        <h1>Skills</h1>
        <div className="top-nav-links">
          <Link href="/settings/skills/new" className="button-link">New skill</Link>
        </div>
      </div>
      <p className="muted">
        A skill is a reusable prompt with a restricted set of tools and secrets it may use — run it directly here,
        or let a scheduled <Link href="/agents">agent</Link> invoke it.
      </p>
      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "failed" && <p className="error-text">{error}</p>}
      <SkillList skills={skills} />
    </main>
  );
}
