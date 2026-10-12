"use client";

import Link from "next/link";

export default function SkillList({ skills }) {
  if (skills.length === 0) {
    return <p className="muted">No skills yet. Create one to give the agent a reusable, tool-driven capability.</p>;
  }

  return (
    <ul className="skill-list">
      {skills.map((skill) => (
        <li key={skill.id} className="skill-list-item-wrap">
          <Link href={`/settings/skills/${skill.id}`} className="skill-list-item">
            <span className="skill-name">{skill.name}</span>
            {skill.description && <span className="skill-description">{skill.description}</span>}
            <span className="skill-meta">
              {skill.providerKey} · {skill.modelName}
              {skill.enabledTools.length > 0 && ` · ${skill.enabledTools.length} tool(s)`}
              {skill.secretKeys.length > 0 && ` · ${skill.secretKeys.length} secret(s)`}
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}
