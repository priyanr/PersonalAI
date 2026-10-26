"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useSelector } from "react-redux";
import { logout } from "@/lib/auth";
import AlertsNavBadge from "@/components/alerts/AlertsNavBadge";

const NAV = [
  { href: "/sessions", label: "Chat", match: ["/sessions"] },
  { href: "/dashboards", label: "Dashboards", match: ["/dashboards"] },
  { href: "/alerts", label: "Alerts", match: ["/alerts"] },
  { href: "/agents", label: "Agents", match: ["/agents"] },
  { href: "/settings/skills", label: "Skills", match: ["/settings/skills", "/skills"] },
  { href: "/models", label: "Models", match: ["/models"] }
];

const SETTINGS = { href: "/settings", label: "Settings" };

function isActive(pathname, match) {
  return match.some((m) => pathname === m || pathname.startsWith(`${m}/`));
}

function navActive(pathname, item) {
  if (item.href === "/dashboards") return isActive(pathname, item.match) || isDashboardPage(pathname);
  if (item.href === "/agents") return isActive(pathname, item.match) && !isDashboardPage(pathname);
  return isActive(pathname, item.match);
}

// /agents/<id>/dashboard belongs to Dashboards, not Agents.
const isDashboardPage = (pathname) => pathname.startsWith("/agents/") && pathname.endsWith("/dashboard");

export default function AppShell({ children }) {
  const pathname = usePathname();
  const authEnabled = useSelector((state) => state.auth.enabled);

  if (pathname === "/auth/callback") return children;

  const skillsActive = isActive(pathname, ["/settings/skills", "/skills"]);
  const settingsActive = pathname.startsWith("/settings") && !skillsActive;

  return (
    <div className="shell">
      <aside className="rail">
        <Link href="/sessions" className="rail-brand">
          PersonalAI
        </Link>
        <nav className="rail-nav" aria-label="Primary">
          {NAV.map((item) => (
            <Link
              key={item.href}
              href={item.href}
              aria-current={navActive(pathname, item) ? "page" : undefined}
            >
              {item.label}
              {item.href === "/alerts" && <AlertsNavBadge />}
            </Link>
          ))}
        </nav>
        <nav className="rail-nav rail-nav-end" aria-label="Account">
          <Link href={SETTINGS.href} aria-current={settingsActive ? "page" : undefined}>
            {SETTINGS.label}
          </Link>
          {authEnabled && (
            <button type="button" className="rail-logout" onClick={logout}>
              Log out
            </button>
          )}
        </nav>
      </aside>
      <div className="stage">{children}</div>
    </div>
  );
}
