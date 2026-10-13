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

    public CameraProgress(string ipAddress, string cameraName)
    {
        this.ipAddress = ipAddress;
        this.cameraName = cameraName;
    }

    public void SetQueue(int fileCount, int alreadyCompleteCount)
    {
        lock (syncRoot)
        {
            totalFiles = fileCount;
            completedFiles = alreadyCompleteCount;
            queueReady = true;
            if (fileCount == 0 || alreadyCompleteCount >= fileCount)
            {
                transferStatus = "Complete";
            }
            else
            {
                transferStatus = "Queued";
            }
        }
    }

    public void SetTransferStatus(string status)
    {
        lock (syncRoot)
        {
            transferStatus = status;
        }
    }

    public void MarkConnected()
    {
        lock (syncRoot)
        {
            connectionStatus = "Connected";
            lastError = null;
            if (currentFile is not null)
            {
                transferStatus = "Downloading";
            }
        }
    }

    public void MarkDisconnected(string error)
    {
        lock (syncRoot)
        {
            connectionStatus = "Disconnected";
            bytesPerSecond = 0;
            lastError = error;
            if (currentFile is not null)
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
            connectionStatus = "Connected";
        }
    }

    public void CompleteFile()
    {
        lock (syncRoot)
        {
            completedFiles++;
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
            double? currentFileProgressPercent = null;
            if (currentFileTotalBytes.HasValue && currentFileTotalBytes.Value > 0)
            {
                currentFileProgressPercent = Math.Min(
                    100d,
                    (double)currentFileBytes * 100d / currentFileTotalBytes.Value);
            }

            double overallProgressPercent = 0;
            if (totalFiles == 0)
            {
                if (transferStatus == "Complete")
                {
                    overallProgressPercent = 100;
                }
            }
            else
            {
                double progressUnits = completedFiles;
                if (currentFileProgressPercent.HasValue)
                {
                    progressUnits += currentFileProgressPercent.Value / 100d;
                }

                overallProgressPercent = Math.Min(100d, progressUnits * 100d / totalFiles);
            }

            return new CameraSnapshot(
                ipAddress,
                cameraName,
                connectionStatus,
                transferStatus,
                totalFiles,
                completedFiles,
                filesLeft,
                queueReady,
                currentFile,
                currentFileBytes,
                currentFileTotalBytes,
                currentFileProgressPercent,
                bytesPerSecond,
                overallProgressPercent,
                lastError);
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
    string? LastError);