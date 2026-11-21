namespace DashCamSync;

internal sealed class CameraProgress
{
    private readonly object syncRoot = new object();
    private readonly string ipAddress;
    private readonly string cameraName;
    private string connectionStatus = "Connecting";
    private string transferStatus = "Scanning";
    private int totalFiles;
    private int completedFiles;
    private bool queueReady;
    private string? currentFile;
    private long currentFileBytes;
    private long? currentFileTotalBytes;
    private double bytesPerSecond;
    private string? lastError;
    private DateTime lastContactUtc = DateTime.UtcNow;
    private long queueTotalBytes;
    private long queueCompletedBytes;
    private bool queueBytesIncomplete;
    private long? filesOnDisk;
    private long? bytesOnDisk;
    private decimal maxSizeGb;
    private string? storageStatus;
    private static readonly TimeSpan ContactTimeout = TimeSpan.FromSeconds(15);

    public event Action? Reconnected;

    public CameraProgress(string ipAddress, string cameraName)
    {
        this.ipAddress = ipAddress;
        this.cameraName = cameraName;
    }

    public string IpAddress => ipAddress;

    public void SetQueue(int fileCount)
    {
        lock (syncRoot)
        {
            totalFiles = fileCount;
            completedFiles = 0;
            queueReady = true;
            if (fileCount == 0)
            {
                transferStatus = "Complete";
            }
            else
            {
                transferStatus = "Queued";
            }
        }
    }

    public void BeginScan()
    {
        lock (syncRoot)
        {
            totalFiles = 0;
            completedFiles = 0;
            queueReady = false;
            currentFile = null;
            currentFileBytes = 0;
            currentFileTotalBytes = null;
            bytesPerSecond = 0;
            queueTotalBytes = 0;
            queueCompletedBytes = 0;
            queueBytesIncomplete = false;
            transferStatus = "Scanning";
            lastError = null;
        }
    }

    public void SetTransferStatus(string status)
    {
        lock (syncRoot)
        {
            transferStatus = status;
        }
    }

    public void SetQueueBytes(long totalBytes, long completedBytes, bool incomplete)
    {
        lock (syncRoot)
        {
            queueTotalBytes = totalBytes;
            queueCompletedBytes = completedBytes;
            queueBytesIncomplete = incomplete;
        }
    }

    public void SetOnDiskStatistics(long fileCount, long totalBytes)
    {
        lock (syncRoot)
        {
            filesOnDisk = fileCount;
            bytesOnDisk = totalBytes;
        }
    }

    public void SetMaxSizeGb(decimal value)
    {
        lock (syncRoot)
        {
            maxSizeGb = value;
        }
    }

    public void SetStorageCleanupStatus(IReadOnlyList<string> deletedFiles)
    {
        if (deletedFiles.Count == 0)
        {
            return;
        }

        lock (syncRoot)
        {
            storageStatus = $"Deleted {deletedFiles.Count:N0} old file{(deletedFiles.Count == 1 ? string.Empty : "s")} to stay within the size limit:\n"
                + string.Join("\n", deletedFiles);
        }
    }

    public void MarkConnected()
    {
        bool wasReconnecting;
        lock (syncRoot)
        {
            wasReconnecting = connectionStatus == "Disconnected" || connectionStatus == "Retrying";
            lastContactUtc = DateTime.UtcNow;
            connectionStatus = "Connected";
            lastError = null;
            if (currentFile is not null && transferStatus != "Paused for deletion")
            {
                transferStatus = "Downloading";
            }
        }

        if (wasReconnecting)
        {
            Reconnected?.Invoke();
        }
    }

    public void MarkDisconnected(string error)
    {
        lock (syncRoot)
        {
            connectionStatus = "Disconnected";
            bytesPerSecond = 0;
            lastError = error;
            if (currentFile is not null && transferStatus != "Paused for deletion")
            {
                transferStatus = "Connection lost";
            }
        }
    }

    public void MarkRetrying(string error)
    {
        lock (syncRoot)
        {
            connectionStatus = "Retrying";
            transferStatus = "Waiting to retry";
            bytesPerSecond = 0;
            lastError = error;
        }
    }

    public void PauseForDeletion()
    {
        lock (syncRoot)
        {
            transferStatus = "Paused for deletion";
            bytesPerSecond = 0;
        }
    }

    public void MarkError(string error)
    {
        lock (syncRoot)
        {
            connectionStatus = "Error";
            transferStatus = "Error";
            bytesPerSecond = 0;
            lastError = error;
        }
    }

    public void BeginFile(string filePath, long initialBytes, long? totalBytes)
    {
        lock (syncRoot)
        {
            currentFile = filePath;
            currentFileBytes = initialBytes;
            currentFileTotalBytes = totalBytes;
            bytesPerSecond = 0;
            transferStatus = "Downloading";
            lastError = null;
        }
    }

    public void UpdateFileProgress(long currentBytes, long? totalBytes, double currentSpeed)
    {
        lock (syncRoot)
        {
            currentFileBytes = currentBytes;
            currentFileTotalBytes = totalBytes;
            bytesPerSecond = currentSpeed;
            lastContactUtc = DateTime.UtcNow;
            connectionStatus = "Connected";
        }
    }

    public void CompleteFile(long fileBytes, bool sizeWasMeasured)
    {
        lock (syncRoot)
        {
            completedFiles++;
            queueCompletedBytes += fileBytes;
            if (!sizeWasMeasured)
            {
                queueTotalBytes += fileBytes;
            }

            currentFile = null;
            currentFileBytes = 0;
            currentFileTotalBytes = null;
            bytesPerSecond = 0;
            transferStatus = "Downloading";
        }
    }

    public void MarkComplete()
    {
        lock (syncRoot)
        {
            completedFiles = totalFiles;
            currentFile = null;
            currentFileBytes = 0;
            currentFileTotalBytes = null;
            bytesPerSecond = 0;
            transferStatus = "Complete";
            lastError = null;
        }
    }

    public void Finish()
    {
        lock (syncRoot)
        {
            if (completedFiles >= totalFiles)
            {
                transferStatus = "Complete";
                currentFile = null;
                currentFileBytes = 0;
                currentFileTotalBytes = null;
                lastError = null;
            }
            else if (transferStatus != "Error")
            {
                transferStatus = "Incomplete";
            }

            bytesPerSecond = 0;
        }
    }

    public CameraSnapshot CreateSnapshot()
    {
        lock (syncRoot)
        {
            int filesLeft = Math.Max(0, totalFiles - completedFiles);
            string reportedConnectionStatus = connectionStatus;
            string reportedTransferStatus = transferStatus;
            double reportedBytesPerSecond = bytesPerSecond;
            string? reportedError = lastError;
            // Neither the probe nor the download has reported in, so the last values are stale.
            if (connectionStatus == "Connected" && DateTime.UtcNow - lastContactUtc > ContactTimeout)
            {
                reportedConnectionStatus = "Disconnected";
                reportedBytesPerSecond = 0;
                if (currentFile is not null && transferStatus != "Paused for deletion")
                {
                    reportedTransferStatus = "Connection lost";
                }

                reportedError ??= "No response from camera.";
            }

            double? currentFileProgressPercent = null;
            long queueDownloadedBytes = queueCompletedBytes;
            if (currentFile is not null)
            {
                queueDownloadedBytes += currentFileBytes;
            }

            long reportedQueueTotalBytes = Math.Max(queueTotalBytes, queueDownloadedBytes);
            if (currentFileTotalBytes.HasValue && currentFileTotalBytes.Value > 0)
            {
                currentFileProgressPercent = Math.Min(
                    100d,
                    (double)currentFileBytes * 100d / currentFileTotalBytes.Value);
            }

            double overallProgressPercent = 0;
            if (totalFiles > 0)
            {
                double progressUnits = completedFiles;
                if (currentFileProgressPercent.HasValue)
                {
                    progressUnits += currentFileProgressPercent.Value / 100d;
                }

                overallProgressPercent = Math.Min(100d, progressUnits * 100d / totalFiles);
                if (!queueBytesIncomplete && reportedQueueTotalBytes > 0 && completedFiles < totalFiles)
                {
                    overallProgressPercent = Math.Min(
                        100d,
                        (double)queueDownloadedBytes * 100d / reportedQueueTotalBytes);
                }
            }

            return new CameraSnapshot(
                ipAddress,
                cameraName,
                reportedConnectionStatus,
                reportedTransferStatus,
                totalFiles,
                completedFiles,
                filesLeft,
                queueReady,
                currentFile,
                currentFileBytes,
                currentFileTotalBytes,
                currentFileProgressPercent,
                reportedBytesPerSecond,
                overallProgressPercent,
                reportedError,
                reportedQueueTotalBytes,
                queueDownloadedBytes,
                queueBytesIncomplete,
                filesOnDisk,
                bytesOnDisk,
                maxSizeGb,
                storageStatus);
        }
    }
}

internal sealed record CameraSnapshot(
    string IpAddress,
    string CameraName,
    string ConnectionStatus,
    string TransferStatus,
    int TotalFiles,
    int CompletedFiles,
    int FilesLeft,
    bool QueueReady,
    string? CurrentFile,
    long CurrentFileBytes,
    long? CurrentFileTotalBytes,
    double? CurrentFileProgressPercent,
    double BytesPerSecond,
    double OverallProgressPercent,
    string? LastError,
    long QueueTotalBytes,
    long QueueDownloadedBytes,
    bool QueueBytesIncomplete,
    long? FilesOnDisk,
    long? BytesOnDisk,
    decimal MaxSizeGb,
    string? StorageStatus);