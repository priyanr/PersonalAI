import { apiUrl } from "./api";
import { authFetch, clearTokens, startLogin } from "./auth";

/**
 * POSTs to an SSE endpoint and invokes onEvent for each decoded `data: {...}` frame.
 * The browser's native EventSource is GET-only, so POST-based streams need a manual
 * fetch() + ReadableStream reader instead.
 */
export async function streamSse(path, body, onEvent, signal) {
  const response = await authFetch(apiUrl(path), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {}),
    signal
  });
  return readSse(response, onEvent);
}

/**
 * GETs an SSE endpoint (re-attaching to a stream already in progress). Resolves false without
 * calling onEvent when the server answers 204 — nothing to attach to.
 */
export async function attachSse(path, onEvent, signal) {
  const response = await authFetch(apiUrl(path), { method: "GET", signal });
  if (response.status === 204) return false;
  await readSse(response, onEvent);
  return true;
}

async function readSse(response, onEvent) {
  if (response.status === 401) {
    clearTokens();
    startLogin();
    return new Promise(() => {}); // navigating away; never resolve
  }
  if (!response.ok || !response.body) {
    const error = new Error(`Request failed with status ${response.status}`);
    error.status = response.status;
    throw error;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;

    buffer += decoder.decode(value, { stream: true });
    let separatorIndex;
    while ((separatorIndex = buffer.indexOf("\n\n")) !== -1) {
      const rawEvent = buffer.slice(0, separatorIndex).trim();
      buffer = buffer.slice(separatorIndex + 2);

      if (!rawEvent.startsWith("data:")) continue;
      const json = rawEvent.slice(5).trim();
      if (!json) continue;

      try {
        onEvent(JSON.parse(json));
      } catch (err) {
        console.error("Failed to parse SSE payload", err, json);
      }
    }
  }
}
