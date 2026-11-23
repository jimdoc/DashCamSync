namespace DashCamSync;

internal sealed record DownloadStatistics(long FileCount, long TotalBytes)
{
    /// <summary>Reads the completed download count and size for a camera.</summary>
    /// <param name="localRoot">The local camera directory to inspect.</param>
    /// <returns>The completed file count and total bytes on disk.</returns>
    public static DownloadStatistics ReadFromDisk(string localRoot)
    {
        CameraDiskUsage usage = CameraDiskUsageReader.Read(localRoot);
        return new DownloadStatistics(usage.FileCount, usage.TotalBytes);
    }
}
