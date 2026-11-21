namespace DashCamSync;

internal sealed record DownloadStatistics(long FileCount, long TotalBytes)
{
    public static DownloadStatistics ReadFromDisk(string localRoot)
    {
        CameraDiskUsage usage = CameraDiskUsageReader.Read(localRoot);
        return new DownloadStatistics(usage.FileCount, usage.TotalBytes);
    }
}
