import { authFetch, clearTokens, startLogin } from "./auth";

const BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5080";

async function readErrorMessage(response) {
  let message = `Request failed with status ${response.status}`;
  try {
    const body = await response.json();
    message = body.message || JSON.stringify(body);
  } catch {
    // response had no JSON body
  }
  return message;
}

async function handleResponse(response) {
  if (response.status === 401) {
    clearTokens();
    startLogin();
    return new Promise(() => {}); // navigating away; never resolve
  }
  if (!response.ok) {
    throw new Error(await readErrorMessage(response));
  }
  if (response.status === 204) return null;
  return response.json();
}

function fileNameFromContentDisposition(header) {
  if (!header) return null;
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(header);
  if (!match) return null;
  // Only the filename*= (RFC 5987) form is actually percent-encoded; a plain filename="..." is sent
  // as-is and can legitimately contain a literal '%' (e.g. "100%.txt"), which decodeURIComponent throws
  // on. Fall back to the raw value rather than let a malformed-looking filename break the download.
  try {
    return decodeURIComponent(match[1]);
  } catch {
    return match[1];
  }
}

export function apiUrl(path) {
  return `${BASE_URL}${path}`;
}

export async function apiGet(path) {
  const response = await authFetch(apiUrl(path));
  return handleResponse(response);
}

export async function apiPost(path, body) {
  const response = await authFetch(apiUrl(path), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {})
  });
  return handleResponse(response);
}

export async function apiPatch(path, body) {
  const response = await authFetch(apiUrl(path), {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {})
  });
  return handleResponse(response);
}

export async function apiPut(path, body) {
  const response = await authFetch(apiUrl(path), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {})
  });
  return handleResponse(response);
}

export async function apiDelete(path) {
  const response = await authFetch(apiUrl(path), { method: "DELETE" });
  return handleResponse(response);
}

// Downloads (single file / zip): the only binary-response endpoints in the app, so this is a separate
// helper rather than a branch inside handleResponse. Returns the filename the server suggested via
// Content-Disposition alongside the blob, so a caller can hand both straight to a synthetic <a download>.
export async function apiGetBlob(path) {
  const response = await authFetch(apiUrl(path));
  if (response.status === 401) {
    clearTokens();
    startLogin();
    return new Promise(() => {});
  }
  if (!response.ok) {
    throw new Error(await readErrorMessage(response));
  }
  const blob = await response.blob();
  const fileName = fileNameFromContentDisposition(response.headers.get("Content-Disposition"));
  return { blob, fileName };
}

// Multipart upload: no Content-Type header is set here so the browser can add its own multipart boundary.
export async function apiPostForm(path, formData) {
  const response = await authFetch(apiUrl(path), {
    method: "POST",
    body: formData
  });
  return handleResponse(response);
}

// POST-with-a-body variant of apiGetBlob (the sandbox zip endpoint needs a body to say which paths to
// include, so a plain apiGetBlob GET doesn't fit).
export async function apiPostBlob(path, body) {
  const response = await authFetch(apiUrl(path), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {})
  });
  if (response.status === 401) {
    clearTokens();
    startLogin();
    return new Promise(() => {});
  }
  if (!response.ok) {
    throw new Error(await readErrorMessage(response));
  }
  const blob = await response.blob();
  const fileName = fileNameFromContentDisposition(response.headers.get("Content-Disposition"));
  return { blob, fileName };
}
