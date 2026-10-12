"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

const TABS = [
  { href: "/settings", label: "General" },
  { href: "/settings/providers", label: "Providers" },
  { href: "/settings/secrets", label: "Secrets" },
  { href: "/settings/skills", label: "Skills" },
  { href: "/settings/tools", label: "Tools" }
];

export default function SettingsTabs() {
  const pathname = usePathname();
  return (
    <div className="settings-tabs">
      {TABS.map((t) => (
        <Link
          key={t.href}
          href={t.href}
          className={pathname === t.href || (t.href !== "/settings" && pathname.startsWith(t.href)) ? "active" : ""}
        >
          {t.label}
        </Link>
      ))}
    </div>
  );
}
