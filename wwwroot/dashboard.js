const cameraList = document.getElementById("camera-list");
const serviceLabel = document.getElementById("service-label");
const serviceDot = document.getElementById("service-dot");
const updatedAt = document.getElementById("updated-at");
const lastError = document.getElementById("last-error");
const cameraCards = new Map();
const activityRefreshIntervalMs = 5000;
let lastActivityRenderAt = 0;
let emptyStateElement = cameraList.querySelector(".empty-state");

/**
 * Escapes a value for insertion into HTML.
 * @param {*} value Value to escape.
 * @returns {string} HTML-escaped text.
 */
function escapeHtml(value) {
  const characters = {
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;"
  };
  return String(value).replace(/[&<>"']/g, character => characters[character]);
}

/**
 * Formats a byte count using binary units.
 * @param {number} bytes Byte count to format.
 * @returns {string} Formatted size.
 */
function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return "0 B";
  }

  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex++;
  }

  let digits = 0;
  if (value < 10 && unitIndex > 0) {
    digits = 1;
  }
  return `${value.toFixed(digits)} ${units[unitIndex]}`;
}

/**
 * Formats a disk size as rounded binary gigabytes.
 * @param {number} bytes Byte count to format.
 * @returns {string} Formatted size.
 */
function formatDiskSize(bytes) {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return "0 GB";
  }

  const value = bytes / (1024 ** 3);
  return `${Math.round(value).toLocaleString()} GB`;
}

/**
 * Formats a transfer speed.
 * @param {number} bytesPerSecond Transfer speed in bytes per second.
 * @returns {string} Formatted speed.
 */
function formatSpeed(bytesPerSecond) {
  return `${formatBytes(bytesPerSecond)}/s`;
}

/**
 * Formats a byte count as rounded binary megabytes.
 * @param {number} bytes Byte count to format.
 * @returns {string} Formatted size.
 */
function formatMegabytes(bytes) {
  return `${Math.round(Math.max(0, bytes) / 1048576).toLocaleString()} MB`;
}

/**
 * Builds a CSS class name from a status.
 * @param {string} prefix Class name prefix.
 * @param {string} status Status to normalize.
 * @returns {string} Status class name.
 */
function stateClass(prefix, status) {
  return `${prefix}-${String(status || "unknown").toLowerCase().replaceAll(" ", "-")}`;
}

/**
 * Updates an element's text only when it has changed.
 * @param {Element} element Element to update.
 * @param {*} value Value to display as text.
 */
function setTextIfChanged(element, value) {
  const text = String(value);
  if (element.textContent !== text) {
    element.textContent = text;
  }
}

/**
 * Creates a camera card and wires its scan, browser, deletion, and storage controls.
 * @param {object} camera Camera snapshot used to identify the camera.
 * @returns {object} Card elements and browser state.
 */
function createCameraCard(camera) {
  const element = document.createElement("article");
  element.className = "camera-card";
  element.innerHTML = `
    <div class="camera-card-header">
      <div>
        <h3 class="camera-name">${escapeHtml(camera.cameraName)}</h3>
        <a class="camera-ip" target="_blank" rel="noopener noreferrer"></a>
      </div>
      <div class="state-group">
        <span class="state-pill connection-pill"><span class="state-dot" aria-hidden="true"></span><span class="connection-label"></span></span>
        <span class="state-pill transfer-pill"><span class="transfer-label"></span></span>
        <button class="camera-rescan-button" type="button">Rescan camera</button>
        <button class="camera-files-button" type="button" aria-expanded="false">Browse files</button>
      </div>
    </div>
    <p class="rescan-message" role="status" aria-live="polite" hidden></p>
    <div class="camera-metrics">
      <div class="camera-metric"><span class="metric-label">Files left</span><strong class="metric-value files-left-value"></strong></div>
      <div class="camera-metric"><span class="metric-label">Current speed</span><strong class="metric-value speed-value"></strong></div>
      <div class="camera-metric"><span class="metric-label">File count</span><strong class="metric-value file-count-value"></strong></div>
      <div class="camera-metric"><span class="metric-label">Files on disk</span><strong class="metric-value disk-files-value"></strong></div>
      <div class="camera-metric"><span class="metric-label">Size on disk</span><strong class="metric-value disk-size-value"></strong></div>
    </div>
    <form class="size-limit-form">
      <label class="size-limit-field">Maximum size (GB)
        <input class="size-limit-input" type="number" min="0" max="8589934591" step="any" value="0" required>
      </label>
      <button class="size-limit-button" type="submit">Save limit</button>
      <span class="size-limit-message" role="status" aria-live="polite"></span>
    </form>
    <p class="statistics-note">0 means no limit. GB uses 1,073,741,824 bytes. Oldest downloaded files are deleted as needed. RO folders are excluded from automatic cleanup, but count toward the limit.</p>
    <p class="storage-status" role="status" aria-live="polite" hidden></p>
    <section class="camera-file-browser" aria-label="Camera files" hidden>
      <div class="file-browser-heading">
        <strong class="file-browser-path"></strong>
        <button class="file-browser-close" type="button">Close</button>
      </div>
      <p class="file-browser-message" role="status" aria-live="polite"></p>
      <p class="file-delete-status" role="status" aria-live="polite" hidden></p>
      <div class="file-browser-actions" hidden>
        <label><input class="file-browser-select-all" type="checkbox"> Select all files</label>
        <span class="file-browser-selection-count"></span>
        <button class="file-browser-delete file-browser-delete-selected" type="button" disabled>Delete selected</button>
      </div>
      <div class="file-browser-list"></div>
    </section>
    <div class="current-file"></div>
    <div class="camera-progress-heading"><span>Current file</span><strong class="current-progress-label"></strong></div>
    <div class="camera-progress-track current-progress-track" role="progressbar" aria-label="Current file progress" aria-valuemin="0" aria-valuemax="100">
      <span class="camera-progress-fill"></span>
    </div>
    <div class="overall-line"><span>Camera queue progress</span><strong class="queue-progress-label"></strong></div>
    <div class="camera-progress-track queue-progress-track" role="progressbar" aria-label="Camera queue progress" aria-valuemin="0" aria-valuemax="100">
      <span class="progress-fill"></span>
    </div>
    <p class="error-message" hidden></p>`;

  const card = {
    element,
    connectionPill: element.querySelector(".connection-pill"),
    connectionLabel: element.querySelector(".connection-label"),
    transferPill: element.querySelector(".transfer-pill"),
    transferLabel: element.querySelector(".transfer-label"),
    filesLeft: element.querySelector(".files-left-value"),
    speed: element.querySelector(".speed-value"),
    fileCount: element.querySelector(".file-count-value"),
    diskFiles: element.querySelector(".disk-files-value"),
    diskSize: element.querySelector(".disk-size-value"),
    sizeLimitInput: element.querySelector(".size-limit-input"),
    sizeLimitButton: element.querySelector(".size-limit-button"),
    sizeLimitMessage: element.querySelector(".size-limit-message"),
    storageStatus: element.querySelector(".storage-status"),
    rescanButton: element.querySelector(".camera-rescan-button"),
    rescanMessage: element.querySelector(".rescan-message"),
    fileBrowserButton: element.querySelector(".camera-files-button"),
    fileBrowser: element.querySelector(".camera-file-browser"),
    fileBrowserPath: element.querySelector(".file-browser-path"),
    fileBrowserMessage: element.querySelector(".file-browser-message"),
    fileDeleteStatus: element.querySelector(".file-delete-status"),
    fileBrowserActions: element.querySelector(".file-browser-actions"),
    fileBrowserSelectAll: element.querySelector(".file-browser-select-all"),
    fileBrowserSelectionCount: element.querySelector(".file-browser-selection-count"),
    fileBrowserDeleteSelected: element.querySelector(".file-browser-delete-selected"),
    fileBrowserList: element.querySelector(".file-browser-list"),
    fileBrowserClose: element.querySelector(".file-browser-close"),
    currentFile: element.querySelector(".current-file"),
    currentProgressLabel: element.querySelector(".current-progress-label"),
    currentProgressTrack: element.querySelector(".current-progress-track"),
    currentProgressFill: element.querySelector(".current-progress-track .camera-progress-fill"),
    queueProgressLabel: element.querySelector(".queue-progress-label"),
    queueProgressTrack: element.querySelector(".queue-progress-track"),
    queueProgressFill: element.querySelector(".queue-progress-track .progress-fill"),
    errorMessage: element.querySelector(".error-message")
  };
  const cameraAddress = element.querySelector(".camera-ip");
  const cameraHost = camera.ipAddress.includes(":") ? `[${camera.ipAddress}]` : camera.ipAddress;
  cameraAddress.href = `http://${cameraHost}/`;
  cameraAddress.textContent = camera.ipAddress;
  card.fileBrowserPathValue = "";
  card.fileBrowserRequestId = 0;
  card.fileDeleteRequestId = 0;
  card.fileDeleteInProgress = false;

  card.rescanButton.addEventListener("click", async () => {
    card.rescanButton.disabled = true;
    card.rescanMessage.hidden = false;
    setTextIfChanged(card.rescanMessage, "Requesting camera rescan…");
    try {
      const response = await fetch(`/api/cameras/${encodeURIComponent(camera.ipAddress)}/scan`, {
        method: "POST"
      });
      const result = await response.json();
      if (!response.ok) {
        throw new Error(result.detail || result.error || `Unable to rescan camera (${response.status})`);
      }

      setTextIfChanged(card.rescanMessage, "Camera rescan requested.");
      lastActivityRenderAt = 0;
      await refreshDashboard();
    } catch (error) {
      setTextIfChanged(card.rescanMessage, `Rescan request failed: ${error.message}`);
    } finally {
      card.rescanButton.disabled = false;
    }
  });

  card.fileBrowserButton.addEventListener("click", async () => {
    card.fileDeleteStatus.hidden = true;
    card.fileDeleteStatus.className = "file-delete-status";
    setTextIfChanged(card.fileDeleteStatus, "");
    card.fileBrowser.hidden = false;
    card.fileBrowserButton.setAttribute("aria-expanded", "true");
    card.fileBrowserButton.disabled = true;
    await loadCameraFiles(card, camera);
    card.fileBrowserButton.disabled = false;
  });
  card.fileBrowserClose.addEventListener("click", () => {
    card.fileBrowser.hidden = true;
    card.fileBrowserButton.setAttribute("aria-expanded", "false");
  });

  card.fileBrowserSelectAll.addEventListener("change", () => {
    for (const checkbox of card.fileBrowserList.querySelectorAll(".file-browser-select")) {
      checkbox.checked = card.fileBrowserSelectAll.checked;
    }
    updateFileSelection(card);
  });

  card.fileBrowserDeleteSelected.addEventListener("click", async () => {
    const paths = Array.from(card.fileBrowserList.querySelectorAll(".file-browser-select:checked"),
      checkbox => checkbox.value);
    if (card.fileDeleteInProgress || paths.length === 0) {
      return;
    }

    const isReadOnly = paths.some(path => path.split("/").slice(0, -1)
      .some(segment => segment.toLocaleLowerCase() === "ro"));
    const warning = isReadOnly ? "These files are in an RO folder.\n\n" : "";
    const names = paths.slice(0, 10).join("\n");
    const remaining = paths.length > 10 ? `\n… and ${paths.length - 10} more` : "";
    if (!window.confirm(`${warning}Permanently delete ${paths.length} selected file${paths.length === 1 ? "" : "s"} from ${camera.cameraName}?\n\n${names}${remaining}`)) {
      return;
    }

    const path = card.fileBrowserPathValue;
    const deleteRequestId = ++card.fileDeleteRequestId;
    setFileDeleteBusy(card, true);
    card.fileDeleteStatus.hidden = false;
    card.fileDeleteStatus.className = "file-delete-status";
    setTextIfChanged(card.fileDeleteStatus, `Deleting ${paths.length} selected file${paths.length === 1 ? "" : "s"}…`);
    try {
      const response = await fetch(
        `/api/cameras/${encodeURIComponent(camera.ipAddress)}/files/delete-batch`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ paths, confirmed: true })
        });
      const result = await response.json();
      if (!response.ok) {
        throw new Error(result.detail || result.error || `Batch delete failed (${response.status})`);
      }

      const requestedPaths = new Set(paths);
      if (!Array.isArray(result.results) || result.results.length !== paths.length
          || result.results.some(item => !requestedPaths.delete(item.path) || typeof item.deleted !== "boolean")) {
        throw new Error("The camera returned an invalid batch deletion result.");
      }

      const failures = result.results.filter(item => !item.deleted);
      const deletedCount = result.results.length - failures.length;
      if (deleteRequestId === card.fileDeleteRequestId) {
        card.fileDeleteStatus.className = `file-delete-status ${failures.length ? "failure" : "success"}`;
        setTextIfChanged(card.fileDeleteStatus,
          `Verified deleted: ${deletedCount} of ${paths.length}.${failures.map(item => `\n${item.path}: ${item.error || "Deletion not confirmed."}`).join("")}`);
      }
    } catch (error) {
      if (deleteRequestId === card.fileDeleteRequestId) {
        card.fileDeleteStatus.className = "file-delete-status failure";
        setTextIfChanged(card.fileDeleteStatus,
          `Batch deletion not confirmed: ${error.message}. Some files may have been deleted.`);
      }
    } finally {
      if (card.fileBrowserPathValue === path) {
        await loadCameraFiles(card, camera, path);
      }
      setFileDeleteBusy(card, false);
    }
  });

  element.querySelector(".size-limit-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (!card.sizeLimitInput.reportValidity()) {
      return;
    }

    card.sizeLimitButton.disabled = true;
    setTextIfChanged(card.sizeLimitMessage, "Saving limit…");
    try {
      const response = await fetch(`/api/cameras/${encodeURIComponent(camera.ipAddress)}/size-limit`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ maxSizeGb: Number(card.sizeLimitInput.value) })
      });
      const result = await response.json();
      if (!response.ok) {
        throw new Error(result.detail || result.error || `Unable to save limit (${response.status})`);
      }

      setTextIfChanged(
        card.sizeLimitMessage,
        `${result.deletedPaths.length > 0
          ? `Saved. Deleted ${result.deletedPaths.length.toLocaleString()} old file${result.deletedPaths.length === 1 ? "" : "s"}.`
          : "Limit saved."}${result.limitStillExceeded ? " Protected RO files keep usage above the limit." : ""}`);
    } catch (error) {
      setTextIfChanged(card.sizeLimitMessage, error.message);
    } finally {
      card.sizeLimitButton.disabled = false;
    }
  });

  return card;
}

/**
 * Updates the file browser's selection count and controls.
 * @param {object} card Camera card containing the file browser.
 */
function updateFileSelection(card) {
  const checkboxes = Array.from(card.fileBrowserList.querySelectorAll(".file-browser-select"));
  const selected = checkboxes.filter(checkbox => checkbox.checked).length;
  card.fileBrowserSelectAll.checked = checkboxes.length > 0 && selected === checkboxes.length;
  card.fileBrowserSelectAll.indeterminate = selected > 0 && selected < checkboxes.length;
  card.fileBrowserSelectAll.disabled = card.fileDeleteInProgress || checkboxes.length === 0;
  card.fileBrowserDeleteSelected.disabled = card.fileDeleteInProgress || selected === 0;
  setTextIfChanged(card.fileBrowserSelectionCount, `${selected} selected`);
}

/**
 * Enables or disables file browser controls during deletion.
 * @param {object} card Camera card containing the file browser.
 * @param {boolean} busy Whether a deletion is in progress.
 */
function setFileDeleteBusy(card, busy) {
  card.fileDeleteInProgress = busy;
  card.fileBrowserButton.disabled = busy;
  card.fileBrowserClose.disabled = busy;
  for (const control of card.fileBrowserList.querySelectorAll("button, input")) {
    control.disabled = busy;
  }
  updateFileSelection(card);
}

/**
 * Loads and renders a camera directory, ignoring stale listing responses.
 * @param {object} card Camera card containing the file browser.
 * @param {object} camera Camera snapshot identifying the camera.
 * @param {string} [path=""] Directory path relative to DCIM.
 * @returns {Promise<void>} A promise that resolves after rendering or reporting an error.
 */
async function loadCameraFiles(card, camera, path = "") {
  const requestId = ++card.fileBrowserRequestId;
  card.fileBrowserPathValue = path;
  setTextIfChanged(card.fileBrowserMessage, "Loading camera files…");
  card.fileBrowserList.replaceChildren();
  card.fileBrowserActions.hidden = true;
  updateFileSelection(card);

  const query = new URLSearchParams({ path });
  try {
    const response = await fetch(
      `/api/cameras/${encodeURIComponent(camera.ipAddress)}/files?${query}`,
      { cache: "no-store" });
    const result = await response.json();
    if (!response.ok) {
      throw new Error(result.detail || result.error || `Unable to list camera files (${response.status})`);
    }

    if (requestId !== card.fileBrowserRequestId) {
      return;
    }

    setTextIfChanged(card.fileBrowserPath, `DCIM/${path}`.replace(/\/$/, ""));
    card.fileBrowserList.replaceChildren();
    if (path) {
      const parentPath = path.split("/").slice(0, -1).join("/");
      const upButton = document.createElement("button");
      upButton.type = "button";
      upButton.className = "file-browser-entry file-browser-up";
      upButton.textContent = "Up one folder";
      upButton.addEventListener("click", () => loadCameraFiles(card, camera, parentPath));
      card.fileBrowserList.append(upButton);
    }

    if (result.length === 0) {
      setTextIfChanged(card.fileBrowserMessage, "This folder is empty.");
      return;
    }

    setTextIfChanged(card.fileBrowserMessage, "");
    card.fileBrowserActions.hidden = !result.some(entry => !entry.isDirectory);
    for (const entry of result) {
      const row = document.createElement("div");
      row.className = "file-browser-row";
      if (entry.isDirectory) {
        const placeholder = document.createElement("span");
        placeholder.className = "file-browser-select-placeholder";
        row.append(placeholder);
      } else {
        const checkbox = document.createElement("input");
        checkbox.type = "checkbox";
        checkbox.className = "file-browser-select";
        checkbox.value = entry.path;
        checkbox.setAttribute("aria-label", `Select ${entry.name}`);
        checkbox.addEventListener("change", () => updateFileSelection(card));
        row.append(checkbox);
      }
      const name = document.createElement(entry.isDirectory ? "button" : "span");
      name.className = "file-browser-entry";
      name.textContent = `${entry.isDirectory ? "[Folder] " : ""}${entry.name}`;
      if (entry.isDirectory) {
        name.type = "button";
        name.addEventListener("click", () => loadCameraFiles(card, camera, entry.path));
      }
      row.append(name);

      if (!entry.isDirectory) {
        const fileSize = document.createElement("span");
        fileSize.className = "file-browser-size";
        if (Number.isFinite(entry.sizeBytes) && entry.sizeBytes >= 0) {
          const prefix = entry.sizeIsApproximate ? "≈" : "";
          const useGigabytes = entry.sizeBytes >= 10 * (1024 ** 3);
          const size = (entry.sizeBytes / (1024 ** (useGigabytes ? 3 : 2))).toLocaleString(undefined, {
            minimumFractionDigits: 2,
            maximumFractionDigits: 6
          });
          fileSize.textContent = `${prefix}${size} ${useGigabytes ? "GB" : "MB"}`;
          if (entry.sizeIsApproximate) {
            fileSize.title = "Approximate size from the camera directory listing";
          }
        } else {
          fileSize.textContent = "Size unavailable";
        }
        row.append(fileSize);
      }

      if (entry.fileTime) {
        const fileTime = document.createElement("time");
        fileTime.className = "file-browser-date";
        fileTime.dateTime = entry.fileTime;
        fileTime.textContent = new Date(entry.fileTime).toLocaleString();
        row.append(fileTime);
      }

      if (!entry.isDirectory) {
        const isReadOnly = entry.path.split("/").slice(0, -1)
          .some(segment => segment.toLocaleLowerCase() === "ro");
        if (isReadOnly) {
          name.textContent += " (RO)";
        }
        const downloadStatus = document.createElement("span");
        downloadStatus.className = "file-browser-download-status";
        switch (entry.downloadStatus) {
          case "downloaded":
            downloadStatus.textContent = "Downloaded";
            downloadStatus.classList.add("downloaded");
            break;
          case "previouslyDownloaded":
            downloadStatus.textContent = "Previously downloaded (not on disk)";
            break;
          case "previouslyDownloadedUnknown":
            downloadStatus.textContent = "Previously downloaded (local copy unknown)";
            break;
          case "notDownloaded":
            downloadStatus.textContent = "Not downloaded";
            break;
          default:
            downloadStatus.textContent = "Download status unavailable";
        }
        name.append(" ", downloadStatus);
        const deleteButton = document.createElement("button");
        deleteButton.type = "button";
        deleteButton.className = "file-browser-delete";
        deleteButton.textContent = "Delete";
        deleteButton.addEventListener("click", async () => {
          if (card.fileDeleteInProgress) {
            return;
          }
          const warning = isReadOnly ? "This file is in an RO folder.\n\n" : "";
          if (!window.confirm(`${warning}Permanently delete this file from ${camera.cameraName}?\n\n${entry.path}`)) {
            return;
          }

          const deleteRequestId = ++card.fileDeleteRequestId;
          setFileDeleteBusy(card, true);
          card.fileDeleteStatus.hidden = false;
          card.fileDeleteStatus.className = "file-delete-status";
          setTextIfChanged(card.fileDeleteStatus, `Deleting ${entry.path}…`);
          try {
            const deleteResponse = await fetch(
              `/api/cameras/${encodeURIComponent(camera.ipAddress)}/files`,
              {
                method: "DELETE",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ path: entry.path, confirmed: true })
              });
            const deleteResult = await deleteResponse.json();
            if (!deleteResponse.ok) {
              throw new Error(deleteResult.detail || deleteResult.error || `Delete failed (${deleteResponse.status})`);
            }

            if (deleteRequestId === card.fileDeleteRequestId) {
              card.fileDeleteStatus.className = "file-delete-status success";
              setTextIfChanged(card.fileDeleteStatus, `Deletion verified: ${deleteResult.deletedPath} is no longer listed on the camera.`);
            }
            if (card.fileBrowserPathValue === path) {
              await loadCameraFiles(card, camera, path);
            }
          } catch (error) {
            if (deleteRequestId === card.fileDeleteRequestId) {
              card.fileDeleteStatus.className = "file-delete-status failure";
              setTextIfChanged(card.fileDeleteStatus, `Deletion not confirmed for ${entry.path}: ${error.message}`);
            }
          } finally {
            setFileDeleteBusy(card, false);
          }
        });
        row.append(deleteButton);
      }

      card.fileBrowserList.append(row);
    }
    setFileDeleteBusy(card, card.fileDeleteInProgress);
  } catch (error) {
    if (requestId === card.fileBrowserRequestId) {
      setTextIfChanged(card.fileBrowserPath, `DCIM/${path}`.replace(/\/$/, ""));
      setTextIfChanged(card.fileBrowserMessage, error.message);
    }
  }
}

/**
 * Updates a progress bar's visual width and accessible value.
 * @param {Element} track Progress bar element.
 * @param {HTMLElement} fill Element displaying the filled portion.
 * @param {number} percentage Progress percentage, bounded to 0–100.
 */
function updateProgress(track, fill, percentage) {
  const boundedPercentage = Math.min(100, Math.max(0, percentage));
  const width = `${boundedPercentage.toFixed(2)}%`;
  if (fill.style.width !== width) {
    fill.style.width = width;
  }

  const accessibleValue = boundedPercentage.toFixed(0);
  if (track.getAttribute("aria-valuenow") !== accessibleValue) {
    track.setAttribute("aria-valuenow", accessibleValue);
  }
}

/**
 * Updates an existing camera card from its latest snapshot.
 * @param {object} card Camera card elements and state.
 * @param {object} camera Latest camera snapshot.
 */
function updateCameraCard(card, camera) {
  const currentFile = camera.currentFile || "No active file";
  let currentProgressText = "Waiting for transfer";
  let currentProgress = 0;
  let currentBytes = formatBytes(camera.currentFileBytes);
  let filesLeftText = camera.filesLeft.toLocaleString();
  let queueProgressText = `${camera.overallProgressPercent.toFixed(0)}%`;
  if (camera.queueBytesIncomplete) {
    queueProgressText = `${queueProgressText} · ${formatMegabytes(camera.queueDownloadedBytes)} downloaded · total size unknown`;
  } else if (camera.queueTotalBytes > 0) {
    queueProgressText = `${queueProgressText} · ${formatMegabytes(camera.queueDownloadedBytes)} / ${formatMegabytes(camera.queueTotalBytes)}`;
  }

  if (!camera.queueReady) {
    filesLeftText = "Scanning";
    queueProgressText = "Scanning";
  }

  if (camera.currentFileProgressPercent !== null && camera.currentFileProgressPercent !== undefined) {
    currentProgress = camera.currentFileProgressPercent;
    currentProgressText = `${currentProgress.toFixed(0)}% · ${currentBytes} / ${formatBytes(camera.currentFileTotalBytes)}`;
  } else if (camera.currentFile) {
    currentProgressText = `${currentBytes} transferred`;
  }

  card.element.classList.toggle("has-error", camera.transferStatus === "Error");
  card.element.classList.toggle("is-retrying", camera.connectionStatus === "Retrying");
  const connectionClass = `state-pill ${stateClass("connection", camera.connectionStatus)}`;
  if (card.connectionPill.className !== connectionClass) {
    card.connectionPill.className = connectionClass;
  }
  const transferClass = `state-pill ${stateClass("transfer", camera.transferStatus)}`;
  if (card.transferPill.className !== transferClass) {
    card.transferPill.className = transferClass;
  }

  setTextIfChanged(card.connectionLabel, camera.connectionStatus);
  setTextIfChanged(card.transferLabel, camera.transferStatus);
  setTextIfChanged(card.filesLeft, filesLeftText);
  setTextIfChanged(card.speed, formatSpeed(camera.bytesPerSecond));
  setTextIfChanged(card.fileCount, `${camera.completedFiles.toLocaleString()} / ${camera.totalFiles.toLocaleString()}`);
  setTextIfChanged(card.diskFiles, camera.filesOnDisk == null ? "Unavailable" : camera.filesOnDisk.toLocaleString());
  setTextIfChanged(card.diskSize, camera.bytesOnDisk == null ? "Unavailable" : formatDiskSize(camera.bytesOnDisk));
  setTextIfChanged(card.storageStatus, camera.storageStatus || "");
  card.storageStatus.hidden = !camera.storageStatus;
  if (document.activeElement !== card.sizeLimitInput) {
    const maxSizeGb = String(camera.maxSizeGb ?? 0);
    if (card.sizeLimitInput.value !== maxSizeGb) {
      card.sizeLimitInput.value = maxSizeGb;
    }
  }
  setTextIfChanged(card.currentFile, currentFile);
  setTextIfChanged(card.currentProgressLabel, currentProgressText);
  updateProgress(card.currentProgressTrack, card.currentProgressFill, currentProgress);
  setTextIfChanged(card.queueProgressLabel, queueProgressText);
  updateProgress(card.queueProgressTrack, card.queueProgressFill, camera.overallProgressPercent);
  setTextIfChanged(card.errorMessage, camera.lastError || "");
  card.errorMessage.hidden = !camera.lastError;
}

/**
 * Creates, orders, updates, or removes camera cards to match the snapshots.
 * @param {object[]} cameras Current camera snapshots.
 */
function renderCameraActivity(cameras) {
  if (cameras.length === 0) {
    for (const card of cameraCards.values()) {
      card.element.remove();
    }
    cameraCards.clear();

    if (emptyStateElement === null) {
      emptyStateElement = document.createElement("div");
      emptyStateElement.className = "empty-state";
      emptyStateElement.textContent = "No cameras are configured";
    }
    if (cameraList.firstElementChild !== emptyStateElement) {
      cameraList.replaceChildren(emptyStateElement);
    }
    return;
  }

  if (emptyStateElement !== null) {
    emptyStateElement.remove();
    emptyStateElement = null;
  }

  const activeCameraIds = new Set(cameras.map(camera => camera.ipAddress));
  for (const [cameraId, card] of cameraCards) {
    if (!activeCameraIds.has(cameraId)) {
      card.element.remove();
      cameraCards.delete(cameraId);
    }
  }

  cameras.forEach((camera, index) => {
    let card = cameraCards.get(camera.ipAddress);
    if (card === undefined) {
      card = createCameraCard(camera);
      cameraCards.set(camera.ipAddress, card);
    }

    const currentElement = cameraList.children[index] || null;
    if (currentElement !== card.element) {
      cameraList.insertBefore(card.element, currentElement);
    }
    updateCameraCard(card, camera);
  });
}

/**
 * Renders aggregate dashboard status and optionally updates the camera cards.
 * @param {object} snapshot Latest dashboard status response.
 * @param {boolean} updateCameraActivity Whether to refresh the camera cards.
 */
function renderDashboard(snapshot, updateCameraActivity) {
  document.getElementById("camera-count").textContent = snapshot.cameraCount.toLocaleString();
  document.getElementById("aggregate-speed").textContent = formatSpeed(snapshot.aggregateBytesPerSecond);
  let filesLeftText = snapshot.filesLeft.toLocaleString();
  if (snapshot.scanningCameras > 0) {
    filesLeftText = `${filesLeftText} known · ${snapshot.scanningCameras} scanning`;
  }
  document.getElementById("files-left").textContent = filesLeftText;
  document.getElementById("files-complete").innerHTML = `${snapshot.completedFiles.toLocaleString()} <span class="summary-secondary">/ ${snapshot.totalFiles.toLocaleString()}</span>`;

  const overallProgress = Math.min(100, Math.max(0, snapshot.overallProgressPercent));
  document.getElementById("overall-progress-label").textContent = `${overallProgress.toFixed(0)}%`;
  document.getElementById("overall-progress-fill").style.width = `${overallProgress.toFixed(2)}%`;
  document.getElementById("overall-progress-track").setAttribute("aria-valuenow", overallProgress.toFixed(0));

  const connectedCameras = snapshot.cameras.filter(camera => camera.connectionStatus === "Connected").length;
  serviceLabel.textContent = `${connectedCameras} / ${snapshot.cameraCount} connected`;
  serviceDot.classList.remove("is-offline");
  serviceDot.classList.add("is-online");
  updatedAt.textContent = new Date(snapshot.updatedAtUtc).toLocaleTimeString();
  const buildVersion = document.getElementById("build-version");
  setTextIfChanged(buildVersion, `version ${snapshot.version} · built ${snapshot.buildTimeUtc}`);
  lastError.textContent = "";

  if (!updateCameraActivity) {
    return;
  }

  renderCameraActivity(snapshot.cameras);
}

/**
 * Fetches dashboard status and displays the result or a connection error.
 * @returns {Promise<void>} A promise that resolves after the dashboard is updated.
 */
async function refreshDashboard() {
  try {
    const response = await fetch("/api/status", { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`Status request failed (${response.status})`);
    }

    const snapshot = await response.json();
    const currentTime = Date.now();
    let updateCameraActivity = false;
    if (currentTime - lastActivityRenderAt >= activityRefreshIntervalMs) {
      updateCameraActivity = true;
      lastActivityRenderAt = currentTime;
    }
    renderDashboard(snapshot, updateCameraActivity);
  } catch (error) {
    serviceLabel.textContent = "Dashboard unavailable";
    serviceDot.classList.remove("is-online");
    serviceDot.classList.add("is-offline");
    lastError.textContent = error.message;
  }
}

refreshDashboard();
window.setInterval(refreshDashboard, 5000);