"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useDispatch } from "react-redux";
import { createSkill } from "@/store/skillsSlice";
import SkillForm from "@/components/skills/SkillForm";

export default function NewSkillPage() {
  const dispatch = useDispatch();
  const router = useRouter();
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);

  async function handleSubmit(values) {
    setSubmitting(true);
    setError(null);
    try {
      const skill = await dispatch(createSkill(values)).unwrap();
      router.push(`/settings/skills/${skill.id}`);
    } catch (err) {
      setError(err.message);
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main>
      <div className="top-nav">
        <h1>New skill</h1>
        <div className="top-nav-links">
          <Link href="/settings/skills">← Skills</Link>
        </div>
      </div>
      <SkillForm onSubmit={handleSubmit} submitLabel="Create skill" submitting={submitting} error={error} />
    </main>
  );
}
