const cameraList = document.getElementById("camera-list");
const serviceLabel = document.getElementById("service-label");
const serviceDot = document.getElementById("service-dot");
const updatedAt = document.getElementById("updated-at");
const lastError = document.getElementById("last-error");
const activityRefreshIntervalMs = 15000;
let lastActivityRenderAt = 0;

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

function formatSpeed(bytesPerSecond) {
  return `${formatBytes(bytesPerSecond)}/s`;
}

function stateClass(prefix, status) {
  return `${prefix}-${String(status || "unknown").toLowerCase().replaceAll(" ", "-")}`;
}

function renderCamera(camera) {
  const currentFile = camera.currentFile || "No active file";
  let currentProgressText = "Waiting for transfer";
  let currentProgress = 0;
  let currentBytes = formatBytes(camera.currentFileBytes);
  let filesLeftText = camera.filesLeft.toLocaleString();
  let queueProgressText = `${camera.overallProgressPercent.toFixed(0)}%`;

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

  let cardClass = "camera-card";
  if (camera.transferStatus === "Error") {
    cardClass += " has-error";
  }
  if (camera.connectionStatus === "Retrying") {
    cardClass += " is-retrying";
  }

  let errorMarkup = "";
  if (camera.lastError) {
    errorMarkup = `<p class="error-message">${escapeHtml(camera.lastError)}</p>`;
  }

  return `
    <article class="${cardClass}">
      <div class="camera-card-header">
        <div>
          <h3 class="camera-name">${escapeHtml(camera.cameraName)}</h3>
          <div class="camera-ip">${escapeHtml(camera.ipAddress)}</div>
        </div>
        <div class="state-group">
          <span class="state-pill ${stateClass("connection", camera.connectionStatus)}">
            <span class="state-dot" aria-hidden="true"></span>
            ${escapeHtml(camera.connectionStatus)}
          </span>
          <span class="state-pill ${stateClass("transfer", camera.transferStatus)}">${escapeHtml(camera.transferStatus)}</span>
        </div>
      </div>
      <div class="camera-metrics">
        <div class="camera-metric"><span class="metric-label">Files left</span><strong class="metric-value">${filesLeftText}</strong></div>
        <div class="camera-metric"><span class="metric-label">Current speed</span><strong class="metric-value">${formatSpeed(camera.bytesPerSecond)}</strong></div>
        <div class="camera-metric"><span class="metric-label">File count</span><strong class="metric-value">${camera.completedFiles.toLocaleString()} / ${camera.totalFiles.toLocaleString()}</strong></div>
      </div>
      <div class="current-file">${escapeHtml(currentFile)}</div>
      <div class="camera-progress-heading"><span>Current file</span><strong>${escapeHtml(currentProgressText)}</strong></div>
      <div class="camera-progress-track" role="progressbar" aria-label="Current file progress" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${currentProgress.toFixed(0)}">
        <span class="camera-progress-fill" style="width:${currentProgress.toFixed(2)}%"></span>
      </div>
      <div class="overall-line"><span>Camera queue progress</span><strong>${queueProgressText}</strong></div>
      <div class="camera-progress-track" role="progressbar" aria-label="Camera queue progress" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${camera.overallProgressPercent.toFixed(0)}">
        <span class="progress-fill" style="width:${camera.overallProgressPercent.toFixed(2)}%"></span>
      </div>
      ${errorMarkup}
    </article>`;
}

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
  lastError.textContent = "";

  if (!updateCameraActivity) {
    return;
  }

  if (snapshot.cameras.length === 0) {
    cameraList.innerHTML = '<div class="empty-state">No cameras are configured</div>';
    return;
  }

  cameraList.innerHTML = snapshot.cameras.map(renderCamera).join("");
}

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