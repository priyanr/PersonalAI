"use client";

import { useState } from "react";
import { useParams, useRouter } from "next/navigation";
import Link from "next/link";
import { useDispatch } from "react-redux";
import { useSkills } from "@/hooks/useSkills";
import { deleteSkill, updateSkill } from "@/store/skillsSlice";
import SkillForm from "@/components/skills/SkillForm";
import SkillRunPanel from "@/components/skills/SkillRunPanel";

export default function SkillDetailPage() {
  const { skillId } = useParams();
  const dispatch = useDispatch();
  const router = useRouter();
  const { skills, status } = useSkills();
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);

  const skill = skills.find((s) => s.id === skillId);

  async function handleSubmit(values) {
    setSubmitting(true);
    setError(null);
    try {
      await dispatch(updateSkill({ id: skillId, ...values })).unwrap();
    } catch (err) {
      setError(err.message);
    } finally {
      setSubmitting(false);
    }
  }

  async function handleDelete() {
    if (!window.confirm(`Delete skill "${skill.name}"? This cannot be undone.`)) return;
    await dispatch(deleteSkill({ id: skillId })).unwrap();
    router.push("/settings/skills");
  }

  if (status === "loading" && !skill) return <main>Loading skill...</main>;
  if (!skill) return <main className="error-text">Skill not found.</main>;

  return (
    <main>
      <div className="top-nav">
        <h1>{skill.name}</h1>
        <div className="top-nav-links">
          <button type="button" className="secondary" onClick={handleDelete}>
            Delete
          </button>
          <Link href="/settings/skills">← Skills</Link>
        </div>
      </div>

      <SkillForm
        key={skill.id}
        initial={skill}
        onSubmit={handleSubmit}
        submitLabel="Save changes"
        submitting={submitting}
        error={error}
      />

      <SkillRunPanel skillId={skill.id} />
    </main>
  );
}
