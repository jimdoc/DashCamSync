using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashCamSync;

internal sealed class CameraSizeLimitStore
{
    public const long BytesPerGigabyte = 1024L * 1024 * 1024;
    private const int CurrentVersion = 2;
    private readonly object syncRoot = new object();
    private readonly string path;
    private Dictionary<string, long> limitsBytes;

    private CameraSizeLimitStore(string path, Dictionary<string, long> limitsBytes)
    {
        this.path = path;
        this.limitsBytes = limitsBytes;
    }

    public static CameraSizeLimitStore Load(string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        string path = Path.Combine(destinationRoot, ".camera-size-limits.json");
        Dictionary<string, long> limitsBytes;
        bool needsMigration = false;
        if (!File.Exists(path))
        {
            limitsBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        }
        else
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("version", out JsonElement version)
                    && version.TryGetInt32(out int parsedVersion)
                    && parsedVersion == CurrentVersion
                    && root.TryGetProperty("limitsBytes", out JsonElement savedLimits))
                {
                    limitsBytes = savedLimits.Deserialize<Dictionary<string, long>>()
                        ?? throw new IOException($"Camera size limits are invalid: {path}");
                }
                else
                {
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        throw new IOException($"Camera size limits are invalid: {path}");
                    }

                    Dictionary<string, long> legacyLimits = root.Deserialize<Dictionary<string, long>>()
                        ?? throw new IOException($"Camera size limits are invalid: {path}");
                    limitsBytes = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, long> limit in legacyLimits)
                    {
                        if (limit.Value < 0 || limit.Value > long.MaxValue / (1024L * 1024))
                        {
                            throw new IOException($"Camera size limits are invalid: {path}");
                        }

                        limitsBytes.Add(
                            limit.Key,
                            checked(limit.Value * 1024L * 1024));
                    }

                    needsMigration = true;
                }
            }
            catch (JsonException ex)
            {
                throw new IOException($"Unable to read camera size limits: {path}", ex);
            }

            if (limitsBytes.Any(limit => string.IsNullOrWhiteSpace(limit.Key) || limit.Value < 0))
            {
                throw new IOException($"Camera size limits are invalid: {path}");
            }
        }

        CameraSizeLimitStore store = new CameraSizeLimitStore(path, limitsBytes);
        if (needsMigration)
        {
            store.Write(limitsBytes);
        }

        return store;
    }

    public long Get(string cameraIpAddress)
    {
        lock (syncRoot)
        {
            return limitsBytes.TryGetValue(cameraIpAddress, out long limit) ? limit : 0;
        }
    }

    public void Set(string cameraIpAddress, long maxSizeBytes)
    {
        if (maxSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSizeBytes));
        }

        lock (syncRoot)
        {
            Dictionary<string, long> updatedLimits = new Dictionary<string, long>(limitsBytes, StringComparer.Ordinal)
            {
                [cameraIpAddress] = maxSizeBytes
            };
            Write(updatedLimits);
            limitsBytes = updatedLimits;
        }
    }

    public static bool TryConvertGbToBytes(decimal maxSizeGb, out long maxSizeBytes)
    {
        maxSizeBytes = 0;
        if (maxSizeGb < 0 || maxSizeGb > (decimal)long.MaxValue / BytesPerGigabyte)
        {
            return false;
        }

        maxSizeBytes = decimal.ToInt64(decimal.Truncate(maxSizeGb * BytesPerGigabyte));
        return true;
    }

    public static decimal ConvertBytesToGb(long bytes)
    {
        return (decimal)bytes / BytesPerGigabyte;
    }

    private void Write(Dictionary<string, long> updatedLimits)
    {
        string temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new LimitsDocument(CurrentVersion, updatedLimits)));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record LimitsDocument(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("limitsBytes")] Dictionary<string, long> LimitsBytes);
}

internal sealed record CameraDiskUsage(long FileCount, long TotalBytes);

internal static class CameraDiskUsageReader
{
    private static readonly EnumerationOptions EnumerationOptions = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static CameraDiskUsage Read(string cameraRoot)
    {
        if (!Directory.Exists(cameraRoot))
        {
            return new CameraDiskUsage(0, 0);
        }

        long fileCount = 0;
        long totalBytes = 0;
        foreach (string file in Directory.EnumerateFiles(cameraRoot, "*", EnumerationOptions))
        {
            if (IsApplicationFile(file))
            {
                continue;
            }

            fileCount = checked(fileCount + 1);
            totalBytes = checked(totalBytes + new FileInfo(file).Length);
        }

        return new CameraDiskUsage(fileCount, totalBytes);
    }

    public static IReadOnlyList<string> TrimOldestFiles(string cameraRoot, long maxSizeBytes)
    {
        if (maxSizeBytes == 0 || !Directory.Exists(cameraRoot))
        {
            return Array.Empty<string>();
        }

        List<FileInfo> files = Directory.EnumerateFiles(cameraRoot, "*", EnumerationOptions)
            .Where(file => !IsApplicationFile(file))
            .Select(file => new FileInfo(file))
            .ToList();
        long totalBytes = files.Aggregate(0L, (total, file) => checked(total + file.Length));
        List<string> deletedFiles = new List<string>();

        foreach (FileInfo file in files
            .Where(file => !IsInReadOnlyFolder(cameraRoot, file.FullName))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.Ordinal))
        {
            if (totalBytes <= maxSizeBytes)
            {
                break;
            }

            if (!file.Exists)
            {
                continue;
            }

            long fileLength = file.Length;
            file.Delete();
            totalBytes -= fileLength;
            deletedFiles.Add(file.FullName);
            Console.WriteLine($"Removed oldest file to enforce the camera size limit: {file.FullName}");
            RemoveEmptyParentDirectories(cameraRoot, Path.GetDirectoryName(file.FullName));
        }

        return deletedFiles;
    }

    private static void RemoveEmptyParentDirectories(string cameraRoot, string? directoryPath)
    {
        string fullCameraRoot = Path.GetFullPath(cameraRoot);
        while (!string.IsNullOrEmpty(directoryPath))
        {
            string fullDirectoryPath = Path.GetFullPath(directoryPath);
            if (string.Equals(fullDirectoryPath, fullCameraRoot, StringComparison.Ordinal)
                || IsInReadOnlyFolder(cameraRoot, fullDirectoryPath, pathIsDirectory: true))
            {
                return;
            }

            if (!Directory.Exists(fullDirectoryPath)
                || Directory.EnumerateFileSystemEntries(fullDirectoryPath).Any())
            {
                return;
            }

            Directory.Delete(fullDirectoryPath);
            Console.WriteLine($"Removed empty folder after size-limit cleanup: {fullDirectoryPath}");
            directoryPath = Path.GetDirectoryName(fullDirectoryPath);
        }
    }

    private static bool IsApplicationFile(string path)
    {
        string name = Path.GetFileName(path);
        return name == ".downloaded-files.json"
            || name == ".downloaded-files.json.tmp"
            || name == ".download-statistics.json"
            || name == ".download-statistics.json.tmp"
            || name == ".camera-size-limits.json"
            || name == ".camera-size-limits.json.tmp"
            || name.EndsWith(".download", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInReadOnlyFolder(string cameraRoot, string filePath)
    {
        return IsInReadOnlyFolder(cameraRoot, filePath, pathIsDirectory: false);
    }

    private static bool IsInReadOnlyFolder(string cameraRoot, string filePath, bool pathIsDirectory)
    {
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(cameraRoot))
            .Equals("RO", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string relativePath = Path.GetRelativePath(cameraRoot, filePath);
        IEnumerable<string> segments = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
        if (!pathIsDirectory)
        {
            segments = segments.SkipLast(1);
        }

        return segments.Any(segment => segment.Equals("RO", StringComparison.OrdinalIgnoreCase));
    }
}
