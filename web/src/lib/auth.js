const BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5080";

function apiUrl(path) {
  return `${BASE_URL}${path}`;
}

const ACCESS_TOKEN_KEY = "pai_access_token";
const REFRESH_TOKEN_KEY = "pai_refresh_token";
const ID_TOKEN_KEY = "pai_id_token";
const EXPIRES_AT_KEY = "pai_expires_at";
const STATE_KEY = "pai_oauth_state";
const PKCE_VERIFIER_KEY = "pai_pkce_verifier";
const RETURN_TO_KEY = "pai_return_to";
const CALLBACK_PATH = "/auth/callback";
// window.location includes the Next.js basePath but router.replace() adds it again, so the saved
// return path is stored without it.
const BASE_PATH = process.env.NEXT_PUBLIC_BASE_PATH || "";

function currentAppPath() {
  const { pathname, search, hash } = window.location;
  const path =
    BASE_PATH && (pathname === BASE_PATH || pathname.startsWith(`${BASE_PATH}/`))
      ? pathname.slice(BASE_PATH.length) || "/"
      : pathname;
  return path + search + hash;
}

export function getAccessToken() {
  try {
    return localStorage.getItem(ACCESS_TOKEN_KEY);
  } catch {
    return null;
  }
}

function getRefreshToken() {
  try {
    return localStorage.getItem(REFRESH_TOKEN_KEY);
  } catch {
    return null;
  }
}

function getIdToken() {
  try {
    return localStorage.getItem(ID_TOKEN_KEY);
  } catch {
    return null;
  }
}

function getExpiresAt() {
  try {
    const value = localStorage.getItem(EXPIRES_AT_KEY);
    return value ? Number(value) : null;
  } catch {
    return null;
  }
}

// A refresh response may carry no new refresh/ID token (the provider isn't rotating them); the stored
// ones then stay valid and are kept rather than overwritten with nothing.
function storeTokens({ accessToken, refreshToken, idToken, expiresIn }) {
  try {
    localStorage.setItem(ACCESS_TOKEN_KEY, accessToken);
    if (refreshToken) localStorage.setItem(REFRESH_TOKEN_KEY, refreshToken);
    if (idToken) localStorage.setItem(ID_TOKEN_KEY, idToken);
    localStorage.setItem(EXPIRES_AT_KEY, String(Date.now() + expiresIn * 1000));
  } catch {
    // localStorage unavailable (private browsing, etc.) -- the session just won't persist
  }
}

export function clearTokens() {
  try {
    localStorage.removeItem(ACCESS_TOKEN_KEY);
    localStorage.removeItem(REFRESH_TOKEN_KEY);
    localStorage.removeItem(ID_TOKEN_KEY);
    localStorage.removeItem(EXPIRES_AT_KEY);
  } catch {
    // ignore
  }
}

export function getAuthStatus() {
  return fetch(apiUrl("/api/auth/status")).then((r) => r.json());
}

function base64Url(bytes) {
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function randomString() {
  return base64Url(crypto.getRandomValues(new Uint8Array(32)));
}

/** RFC 7636 S256 challenge, or null where WebCrypto's digest isn't available (plain-http origins). */
async function pkceChallenge(verifier) {
  if (!crypto.subtle) return null;
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64Url(new Uint8Array(digest));
}

/** Only a same-origin relative path (no `//host` or `/\host` open redirect, never the callback itself). */
function safeReturnPath(value) {
  if (typeof value !== "string" || !value.startsWith("/")) return null;
  if (value.startsWith("//") || value.startsWith("/\\")) return null;
  if (value.startsWith(CALLBACK_PATH)) return null;
  return value;
}

let loginStarted = false;

/**
 * Redirects the browser to the OAuth2 provider's consent page, with a CSRF `state` and (where the
 * browser can compute it) a PKCE challenge whose verifier is kept in sessionStorage until the callback.
 * The current page is remembered too, so the callback can bring the user back to it.
 * Several failing requests may all call this at once; only the first one navigates.
 */
export async function startLogin() {
  if (loginStarted) return;
  loginStarted = true;

  const state = randomString();
  const verifier = randomString();
  const challenge = await pkceChallenge(verifier);
  try {
    sessionStorage.setItem(STATE_KEY, state);
    if (challenge) sessionStorage.setItem(PKCE_VERIFIER_KEY, verifier);
    else sessionStorage.removeItem(PKCE_VERIFIER_KEY);
    const returnTo = currentAppPath();
    if (!returnTo.startsWith(CALLBACK_PATH)) sessionStorage.setItem(RETURN_TO_KEY, returnTo);
  } catch {
    // ignore -- worst case the state check below fails and the user retries login
  }
  const challengeParam = challenge ? `&code_challenge=${encodeURIComponent(challenge)}` : "";
  window.location.href = apiUrl(`/api/auth/login?state=${encodeURIComponent(state)}${challengeParam}`);
}

/**
 * Called from the frontend's own /auth/callback route once the provider redirects back. Resolves to
 * the page the user was on when login started (or null if unknown), for the callback to return to.
 */
export async function completeLogin(code, state) {
  let expectedState = null;
  let codeVerifier = null;
  let returnTo = null;
  try {
    expectedState = sessionStorage.getItem(STATE_KEY);
    codeVerifier = sessionStorage.getItem(PKCE_VERIFIER_KEY);
    returnTo = sessionStorage.getItem(RETURN_TO_KEY);
  } catch {
    // ignore
  }
  if (!code || !state || !expectedState || state !== expectedState) {
    throw new Error("Login state mismatch -- please try logging in again.");
  }
  try {
    sessionStorage.removeItem(STATE_KEY);
    sessionStorage.removeItem(PKCE_VERIFIER_KEY);
    sessionStorage.removeItem(RETURN_TO_KEY);
  } catch {
    // ignore
  }

  const response = await fetch(apiUrl("/api/auth/exchange"), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ code, codeVerifier })
  });
  if (!response.ok) throw new Error("Login failed while exchanging the authorization code.");
  storeTokens(await response.json());
  return safeReturnPath(returnTo);
}

function isAccessTokenFresh() {
  const expiresAt = getExpiresAt();
  return Boolean(expiresAt && Date.now() < expiresAt - 30_000);
}

const REFRESH_LOCK_NAME = "pai-token-refresh";
const REFRESH_REQUEST_TIMEOUT_MS = 10_000;
const REFRESH_LOCK_WAIT_TIMEOUT_MS = 15_000;

// The provider rotates refresh tokens (each is single-use), so two refreshes racing with the same
// token used to mean one of them got rejected and wiped the tokens the other had just stored --
// a spurious logout. Refreshes are therefore single-flight: one shared promise per tab, and a Web
// Lock across tabs. Only the refresh itself runs inside the lock (never a navigation, a
// never-resolving promise, or a nested lock request -- Web Locks aren't re-entrant), the request
// has a timeout, and waiting for the lock has one too, so a stuck tab can't block the others.
let refreshInFlight = null;

/**
 * Refreshes the tokens unless someone else already replaced `staleToken`. Resolves to
 * `{ token, rejected }`: `rejected` is true only when the provider refused the refresh token (the
 * session is really over and tokens were cleared); a network error, timeout or 503 keeps the
 * existing tokens and resolves to the current access token so a later call can try again.
 */
function refreshTokens(staleToken) {
  refreshInFlight ??= withRefreshLock(() => refreshUnlessReplaced(staleToken)).finally(() => {
    refreshInFlight = null;
  });
  return refreshInFlight;
}

async function withRefreshLock(fn) {
  if (typeof navigator === "undefined" || !navigator.locks) return fn(); // plain-http origin: in-tab only
  try {
    return await navigator.locks.request(
      REFRESH_LOCK_NAME,
      { signal: AbortSignal.timeout(REFRESH_LOCK_WAIT_TIMEOUT_MS) },
      fn
    );
  } catch {
    return { token: getAccessToken(), rejected: false }; // lock wait timed out -- use what's stored
  }
}

async function refreshUnlessReplaced(staleToken) {
  // Re-read after acquiring the lock: another tab (or an earlier caller) may already have rotated.
  const current = getAccessToken();
  if (!current) return { token: null, rejected: true };
  if (current !== staleToken && isAccessTokenFresh()) return { token: current, rejected: false };

  const refreshToken = getRefreshToken();
  if (!refreshToken) return { token: current, rejected: false };

  let response;
  try {
    response = await fetch(apiUrl("/api/auth/refresh"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken }),
      signal: AbortSignal.timeout(REFRESH_REQUEST_TIMEOUT_MS)
    });
  } catch {
    return { token: current, rejected: false }; // network hiccup or timeout
  }

  if (response.status === 401) {
    // Only clear if nobody stored a newer pair meanwhile (possible when Web Locks are unavailable).
    if (getRefreshToken() !== refreshToken) return { token: getAccessToken(), rejected: false };
    clearTokens();
    return { token: null, rejected: true };
  }
  if (!response.ok) return { token: current, rejected: false }; // 503: provider unavailable

  try {
    const tokens = await response.json();
    storeTokens(tokens);
    return { token: tokens.accessToken, rejected: false };
  } catch {
    return { token: current, rejected: false };
  }
}

/**
 * Returns a usable access token, transparently refreshing it first if it's within 30s of
 * expiring. Returns null only when there's no session at all (never logged in, or the refresh
 * token itself was rejected) -- callers should treat that as "not authenticated".
 */
export async function getValidAccessToken() {
  const token = getAccessToken();
  if (!token) return null;
  if (isAccessTokenFresh()) return token;
  return (await refreshTokens(token)).token;
}

/**
 * fetch() with the bearer token attached. On a 401 it refreshes (unless another caller already
 * did) and retries once, since a 401 can just mean the token was rotated out from under this
 * request. If that refresh couldn't reach the provider, it throws instead of returning the 401,
 * so a provider outage surfaces as an error rather than a forced logout. A 401 that does come back
 * means the session is really gone.
 */
export async function authFetch(url, init = {}) {
  const send = (token) =>
    fetch(url, { ...init, headers: token ? { ...init.headers, Authorization: `Bearer ${token}` } : init.headers });

  const token = await getValidAccessToken();
  const response = await send(token);
  if (response.status !== 401 || !token) return response;

  const refreshed = await refreshTokens(token);
  if (refreshed.rejected || !refreshed.token) return response;
  if (refreshed.token === token) {
    const err = new Error("Couldn't refresh your session -- the login provider may be unavailable. Try again shortly.");
    err.status = 503;
    throw err;
  }
  return send(refreshed.token);
}

export async function getMe() {
  const response = await authFetch(apiUrl("/api/auth/me"));
  if (!response.ok) {
    if (response.status === 401) clearTokens();
    const err = new Error(`Request failed with status ${response.status}`);
    err.status = response.status;
    throw err;
  }
  return response.json();
}

/**
 * Revokes the refresh token, then ends the provider's own session too when it supports OIDC
 * RP-initiated logout -- otherwise the next login would silently sign the same user straight back in.
 */
export async function logout() {
  const refreshToken = getRefreshToken();
  const idToken = getIdToken();
  clearTokens();

  let endSessionUrl = null;
  try {
    const response = await fetch(apiUrl("/api/auth/logout"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken, idToken })
    });
    if (response.ok) endSessionUrl = (await response.json())?.endSessionUrl ?? null;
  } catch {
    // best-effort
  }

  if (endSessionUrl) window.location.href = endSessionUrl;
  else startLogin();
}
