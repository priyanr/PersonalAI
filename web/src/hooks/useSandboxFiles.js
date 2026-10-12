"use client";

import { useCallback, useEffect, useState } from "react";
import { apiGet, apiGetBlob, apiPost, apiPostBlob, apiPostForm, apiPut } from "@/lib/api";

function basename(path) {
  const idx = path.lastIndexOf("/");
  return idx === -1 ? path : path.slice(idx + 1);
}

function parentOf(path) {
  const idx = path.lastIndexOf("/");
  return idx <= 0 ? "." : path.slice(0, idx);
}

function triggerDownload(blob, fileName) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName || "download";
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

// All state for one open SandboxFileBrowser instance: the current directory listing, the selection used
// by "download selected" / "delete selected", and whichever single file is open in the view/edit panel.
// Deliberately local hook state rather than a Redux slice -- this is per-open-modal UI state (current
// directory, a pending edit, an in-flight upload), not a shared entity the rest of the app needs to read.
export function useSandboxFiles(ownerId) {
  const [path, setPath] = useState(".");
  const [entries, setEntries] = useState([]);
  const [listLoading, setListLoading] = useState(true);
  const [listError, setListError] = useState(null);
  const [selected, setSelected] = useState(() => new Set());
  const [openFile, setOpenFile] = useState(null);
  const [busy, setBusy] = useState(null);
  const [actionError, setActionError] = useState(null);

  const refresh = useCallback(async () => {
    setListLoading(true);
    setListError(null);
    try {
      const params = new URLSearchParams({ path, recursive: "false" });
      const result = await apiGet(`/api/sandbox/${ownerId}/files?${params.toString()}`);
      setEntries(result);
    } catch (err) {
      setListError(err.message);
      setEntries([]);
    } finally {
      setListLoading(false);
    }
  }, [ownerId, path]);

  useEffect(() => {
    refresh();
  }, [refresh]);

  const navigateTo = useCallback((newPath) => {
    setSelected(new Set());
    setOpenFile(null);
    setPath(newPath || ".");
  }, []);

  const goUp = useCallback(() => navigateTo(parentOf(path)), [navigateTo, path]);

  const toggleSelected = useCallback((entryPath) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(entryPath)) next.delete(entryPath);
      else next.add(entryPath);
      return next;
    });
  }, []);

  const clearSelection = useCallback(() => setSelected(new Set()), []);

  const withBusy = useCallback(async (message, fn) => {
    setBusy(message);
    setActionError(null);
    try {
      await fn();
    } catch (err) {
      setActionError(err.message);
    } finally {
      setBusy(null);
    }
  }, []);

  const openEntry = useCallback(
    async (entryPath) => {
      setOpenFile({ path: entryPath, loading: true });
      try {
        const dto = await apiGet(`/api/sandbox/${ownerId}/files/content?path=${encodeURIComponent(entryPath)}`);
        setOpenFile({
          path: entryPath,
          loading: false,
          isText: dto.isText,
          content: dto.content ?? "",
          originalContent: dto.content ?? "",
          truncated: dto.truncated,
          totalSize: dto.totalSize,
          error: null
        });
      } catch (err) {
        setOpenFile({ path: entryPath, loading: false, error: err.message });
      }
    },
    [ownerId]
  );

  const closeFile = useCallback(() => setOpenFile(null), []);

  const updateDraft = useCallback((text) => {
    setOpenFile((f) => (f ? { ...f, content: text } : f));
  }, []);

  const saveFile = useCallback(async () => {
    if (!openFile) return;
    await withBusy("Saving...", async () => {
      await apiPut(`/api/sandbox/${ownerId}/files/content`, { path: openFile.path, content: openFile.content });
      setOpenFile((f) => (f ? { ...f, originalContent: f.content } : f));
      await refresh();
    });
  }, [ownerId, openFile, refresh, withBusy]);

  const downloadEntry = useCallback(
    async (entryPath) => {
      await withBusy("Downloading...", async () => {
        const { blob, fileName } = await apiGetBlob(`/api/sandbox/${ownerId}/files/download?path=${encodeURIComponent(entryPath)}`);
        triggerDownload(blob, fileName || basename(entryPath));
      });
    },
    [ownerId, withBusy]
  );

  const downloadZip = useCallback(
    async (paths) => {
      await withBusy("Building zip...", async () => {
        const { blob, fileName } = await apiPostBlob(`/api/sandbox/${ownerId}/files/zip`, {
          paths: paths && paths.length > 0 ? paths : null
        });
        triggerDownload(blob, fileName || "sandbox.zip");
      });
    },
    [ownerId, withBusy]
  );

  const uploadFiles = useCallback(
    async (fileList) => {
      await withBusy(fileList.length > 1 ? "Uploading files..." : "Uploading...", async () => {
        for (const file of fileList) {
          const form = new FormData();
          form.append("path", path);
          form.append("file", file);
          await apiPostForm(`/api/sandbox/${ownerId}/files/upload`, form);
        }
        await refresh();
      });
    },
    [ownerId, path, refresh, withBusy]
  );

  const createFolder = useCallback(
    async (name) => {
      const target = path === "." ? name : `${path}/${name}`;
      await withBusy("Creating folder...", async () => {
        await apiPost(`/api/sandbox/${ownerId}/files/mkdir`, { path: target });
        await refresh();
      });
    },
    [ownerId, path, refresh, withBusy]
  );

  const deleteEntries = useCallback(
    async (paths) => {
      await withBusy(paths.length > 1 ? "Deleting..." : "Deleting...", async () => {
        for (const entryPath of paths) {
          await apiPost(`/api/sandbox/${ownerId}/files/delete`, { path: entryPath, recursive: true });
        }
        clearSelection();
        if (openFile && paths.includes(openFile.path)) setOpenFile(null);
        await refresh();
      });
    },
    [ownerId, openFile, clearSelection, refresh, withBusy]
  );

  return {
    path,
    entries,
    listLoading,
    listError,
    selected,
    toggleSelected,
    clearSelection,
    navigateTo,
    goUp,
    openFile,
    openEntry,
    closeFile,
    updateDraft,
    saveFile,
    downloadEntry,
    downloadZip,
    uploadFiles,
    createFolder,
    deleteEntries,
    busy,
    actionError,
    refresh
  };
}
