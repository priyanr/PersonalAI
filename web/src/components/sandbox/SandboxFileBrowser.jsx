"use client";

import { useRef, useState } from "react";
import { useSandboxFiles } from "@/hooks/useSandboxFiles";

function basename(path) {
  const idx = path.lastIndexOf("/");
  return idx === -1 ? path : path.slice(idx + 1);
}

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

// Full-screen modal over a running sandbox's /work directory: browse, view/edit text files,
// download/upload, and download a selection (or everything) as a zip. ownerId is a ChatSession id or an
// AgentRun id -- the caller only renders this once useSandboxStatus(ownerId).canBrowse is true.
export default function SandboxFileBrowser({ ownerId, onClose }) {
  const {
    path,
    entries,
    listLoading,
    listError,
    selected,
    toggleSelected,
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
  } = useSandboxFiles(ownerId);

  const fileInputRef = useRef(null);
  const [newFolderOpen, setNewFolderOpen] = useState(false);
  const [newFolderName, setNewFolderName] = useState("");
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const sortedEntries = [...entries].sort((a, b) => {
    if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
    return basename(a.path).localeCompare(basename(b.path));
  });
  const breadcrumbs = path === "." ? [] : path.split("/");
  const isDirty = Boolean(openFile?.isText && openFile.content !== openFile.originalContent);

  function handleRowClick(entry) {
    if (entry.isDirectory) navigateTo(entry.path);
    else openEntry(entry.path);
  }

  function handleUploadChange(e) {
    const files = e.target.files;
    if (files && files.length > 0) uploadFiles(files);
    e.target.value = "";
  }

  async function handleCreateFolder() {
    const name = newFolderName.trim();
    if (!name) return;
    await createFolder(name);
    setNewFolderName("");
    setNewFolderOpen(false);
  }

  async function handleDeleteSelected() {
    if (!confirmingDelete) {
      setConfirmingDelete(true);
      return;
    }
    await deleteEntries([...selected]);
    setConfirmingDelete(false);
  }

  return (
    <div className="sandbox-browser-overlay" role="dialog" aria-modal="true" aria-label="Sandbox files">
      <div className="sandbox-browser">
        <div className="sandbox-browser-header">
          <h2>Sandbox files</h2>
          <button type="button" className="secondary" onClick={onClose}>
            Close
          </button>
        </div>

        <div className="sandbox-browser-toolbar">
          <button type="button" className="secondary" onClick={goUp} disabled={path === "."}>
            ← Up
          </button>
          <span className="sandbox-browser-breadcrumb">
            <button type="button" className="link-button" onClick={() => navigateTo(".")}>
              /work
            </button>
            {breadcrumbs.map((segment, i) => {
              const segPath = breadcrumbs.slice(0, i + 1).join("/");
              return (
                <span key={segPath}>
                  {" / "}
                  <button type="button" className="link-button" onClick={() => navigateTo(segPath)}>
                    {segment}
                  </button>
                </span>
              );
            })}
          </span>

          <div className="sandbox-browser-toolbar-actions">
            <button type="button" className="secondary" onClick={() => setNewFolderOpen((v) => !v)}>
              New folder
            </button>
            <button type="button" className="secondary" onClick={() => fileInputRef.current?.click()}>
              Upload
            </button>
            <input ref={fileInputRef} type="file" multiple hidden onChange={handleUploadChange} />
            <button type="button" className="secondary" onClick={() => downloadZip([...selected])} disabled={selected.size === 0}>
              Download selected (zip)
            </button>
            <button type="button" className="secondary" onClick={() => downloadZip(null)}>
              Download all (zip)
            </button>
            <button type="button" className="secondary" onClick={handleDeleteSelected} disabled={selected.size === 0}>
              {confirmingDelete ? "Confirm delete?" : "Delete selected"}
            </button>
            <button type="button" className="secondary" onClick={refresh}>
              Refresh
            </button>
          </div>
        </div>

        {newFolderOpen && (
          <div className="sandbox-browser-new-folder">
            <input
              type="text"
              placeholder="Folder name"
              value={newFolderName}
              onChange={(e) => setNewFolderName(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && handleCreateFolder()}
            />
            <button type="button" onClick={handleCreateFolder}>
              Create
            </button>
            <button type="button" className="secondary" onClick={() => setNewFolderOpen(false)}>
              Cancel
            </button>
          </div>
        )}

        {busy && <p className="muted">{busy}</p>}
        {actionError && <p className="error-text">{actionError}</p>}

        <div className="sandbox-browser-body">
          <div className="sandbox-browser-list">
            {listLoading && <p className="muted">Loading...</p>}
            {listError && <p className="error-text">{listError}</p>}
            {!listLoading && !listError && sortedEntries.length === 0 && <p className="muted">(empty directory)</p>}
            {!listLoading && sortedEntries.length > 0 && (
              <table className="sandbox-browser-table">
                <thead>
                  <tr>
                    <th></th>
                    <th>Name</th>
                    <th>Size</th>
                    <th>Modified</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {sortedEntries.map((entry) => (
                    <tr key={entry.path} className={openFile?.path === entry.path ? "is-open" : ""}>
                      <td>
                        <input type="checkbox" checked={selected.has(entry.path)} onChange={() => toggleSelected(entry.path)} />
                      </td>
                      <td>
                        <button type="button" className="link-button sandbox-browser-entry-name" onClick={() => handleRowClick(entry)}>
                          {basename(entry.path)}
                          {entry.isDirectory ? "/" : ""}
                        </button>
                      </td>
                      <td className="muted">{entry.isDirectory ? "" : formatSize(entry.size)}</td>
                      <td className="muted">{new Date(entry.modifiedAtUtc).toLocaleString()}</td>
                      <td>
                        {!entry.isDirectory && (
                          <button type="button" className="secondary" onClick={() => downloadEntry(entry.path)}>
                            Download
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          {openFile && (
            <div className="sandbox-browser-viewer">
              <div className="sandbox-browser-viewer-header">
                <strong>{basename(openFile.path)}</strong>
                <div className="sandbox-browser-viewer-actions">
                  <button type="button" className="secondary" onClick={() => downloadEntry(openFile.path)}>
                    Download
                  </button>
                  <button type="button" className="secondary" onClick={closeFile}>
                    Close
                  </button>
                </div>
              </div>

              {openFile.loading && <p className="muted">Loading...</p>}
              {openFile.error && <p className="error-text">{openFile.error}</p>}

              {!openFile.loading && !openFile.error && openFile.isText && (
                <>
                  <textarea
                    className="sandbox-browser-editor"
                    value={openFile.content}
                    onChange={(e) => updateDraft(e.target.value)}
                    spellCheck={false}
                  />
                  <button type="button" onClick={saveFile} disabled={!isDirty || Boolean(busy)}>
                    {busy === "Saving..." ? "Saving..." : "Save"}
                  </button>
                </>
              )}

              {!openFile.loading && !openFile.error && openFile.isText === false && (
                <p className="muted">Binary file ({formatSize(openFile.totalSize)}) — preview not available. Use Download instead.</p>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
