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

    /// <summary>Creates a store for persisted per-camera limits.</summary>
    /// <param name="path">The path to the limits file.</param>
    /// <param name="limitsBytes">The initial limits keyed by camera IP address.</param>
    private CameraSizeLimitStore(string path, Dictionary<string, long> limitsBytes)
    {
        this.path = path;
        this.limitsBytes = limitsBytes;
    }

    /// <summary>Loads camera limits and migrates legacy megabyte values when needed.</summary>
    /// <param name="destinationRoot">The download root containing the limits file.</param>
    /// <returns>The loaded limit store.</returns>
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

    /// <summary>Retrieves a camera's configured storage limit.</summary>
    /// <param name="cameraIpAddress">The camera's IP address.</param>
    /// <returns>The limit in bytes, or zero if none is configured.</returns>
    public long Get(string cameraIpAddress)
    {
        lock (syncRoot)
        {
            return limitsBytes.TryGetValue(cameraIpAddress, out long limit) ? limit : 0;
        }
    }

    /// <summary>Persists a camera's storage limit.</summary>
    /// <param name="cameraIpAddress">The camera's IP address.</param>
    /// <param name="maxSizeBytes">The nonnegative limit in bytes, or zero for unlimited.</param>
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

    /// <summary>Converts a nonnegative binary-gigabyte limit to bytes when it fits.</summary>
    /// <param name="maxSizeGb">The limit in binary gigabytes.</param>
    /// <param name="maxSizeBytes">Receives the converted limit in bytes.</param>
    /// <returns>True if the value is valid and representable; otherwise, false.</returns>
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

    /// <summary>Converts bytes to binary gigabytes.</summary>
    /// <param name="bytes">The byte count to convert.</param>
    /// <returns>The equivalent size in binary gigabytes.</returns>
    public static decimal ConvertBytesToGb(long bytes)
    {
        return (decimal)bytes / BytesPerGigabyte;
    }

    /// <summary>Persists the supplied limits using a temporary file.</summary>
    /// <param name="updatedLimits">The camera IP addresses and byte limits to write.</param>
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

    /// <summary>Counts completed files and their bytes under a camera's local root.</summary>
    /// <param name="cameraRoot">The local camera directory to inspect.</param>
    /// <returns>The file count and total size, excluding application files.</returns>
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

    /// <summary>Deletes oldest unprotected files until usage is within the configured limit and removes empty folders.</summary>
    /// <param name="cameraRoot">The local camera directory to trim.</param>
    /// <param name="maxSizeBytes">The limit in bytes, or zero to disable cleanup.</param>
    /// <returns>The paths of files deleted during cleanup.</returns>
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
        }

        RemoveEmptyDirectories(cameraRoot);
        return deletedFiles;
    }

    /// <summary>Removes empty directories without deleting the camera root or protected folders.</summary>
    /// <param name="cameraRoot">The local camera directory that bounds cleanup.</param>
    private static void RemoveEmptyDirectories(string cameraRoot)
    {
        List<string> directories = Directory.EnumerateDirectories(cameraRoot, "*", EnumerationOptions)
            .OrderByDescending(path => path.Length)
            .ToList();
        foreach (string directoryPath in directories)
        {
            if (IsInReadOnlyFolder(cameraRoot, directoryPath, pathIsDirectory: true))
            {
                continue;
            }

            if (!Directory.Exists(directoryPath)
                || Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                continue;
            }

            Directory.Delete(directoryPath);
            Console.WriteLine($"Removed empty folder after size-limit cleanup: {directoryPath}");
        }
    }

    /// <summary>Identifies application metadata and incomplete downloads excluded from disk usage.</summary>
    /// <param name="path">The file path to inspect.</param>
    /// <returns>True if the file is application metadata or an incomplete download.</returns>
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

    /// <summary>Checks whether a file is inside a protected RO folder.</summary>
    /// <param name="cameraRoot">The local camera root.</param>
    /// <param name="filePath">The file path to inspect.</param>
    /// <returns>True if the file is inside an RO folder.</returns>
    private static bool IsInReadOnlyFolder(string cameraRoot, string filePath)
    {
        return IsInReadOnlyFolder(cameraRoot, filePath, pathIsDirectory: false);
    }

    /// <summary>Checks whether a file or directory is inside a protected RO folder.</summary>
    /// <param name="cameraRoot">The local camera root.</param>
    /// <param name="filePath">The file or directory path to inspect.</param>
    /// <param name="pathIsDirectory">Whether the supplied path itself is a directory.</param>
    /// <returns>True if the path is in or is an RO folder.</returns>
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
