"use client";

import { useEffect, useRef } from "react";
import { usePathname } from "next/navigation";
import { useDispatch, useSelector } from "react-redux";
import { fetchAuthState } from "@/store/authSlice";
import { getMe, startLogin } from "@/lib/auth";

const CALLBACK_PATH = "/auth/callback";

// Gates the whole app behind login when Auth:Enabled is true on the backend. While the check is
// in flight (or the visitor is anonymous and being redirected to /api/auth/login), renders
// nothing rather than flashing protected content.
//
// This layout only remounts on a full browser page load, so the initial fetchAuthState() call
// alone wouldn't catch a token that goes stale mid-session (e.g. the refresh token gets revoked)
// during client-side navigation between pages. The pathname effect re-verifies on every route
// change so navigating to any page always re-checks token validity.
//
// /auth/callback is exempt from all of this: it's the page that *creates* the session (exchanging
// the provider's code for tokens). Gating it too caused a redirect loop -- fetchAuthState() would
// call getMe() before the callback page's own completeLogin() had stored a token, see no valid
// session, and call startLogin(), bouncing the browser straight back to the OAuth provider mid-exchange.
export default function AuthGate({ children }) {
  const dispatch = useDispatch();
  const status = useSelector((state) => state.auth.status);
  const enabled = useSelector((state) => state.auth.enabled);
  const pathname = usePathname();
  const skipNextCheck = useRef(true);
  const isCallbackRoute = pathname === CALLBACK_PATH;

  useEffect(() => {
    if (isCallbackRoute) return;
    dispatch(fetchAuthState());
  }, [dispatch, isCallbackRoute]);

  useEffect(() => {
    if (isCallbackRoute || status !== "ready" || !enabled) return;
    // The initial fetchAuthState() above already verified the token for this pathname.
    if (skipNextCheck.current) {
      skipNextCheck.current = false;
      return;
    }
    // Only a 401 means the session is gone; a provider outage (503) or network error shouldn't
    // throw away a perfectly good session -- the next request will simply try again.
    getMe().catch((err) => {
      if (err.status === 401) startLogin();
    });
  }, [pathname, status, enabled, isCallbackRoute]);

  if (isCallbackRoute) return children;
  if (status !== "ready") return null;
  return children;
}
