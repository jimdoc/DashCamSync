using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DashCamSync;

/// <summary>
/// Docker configuration is in compose.yaml. Set each camera using a consecutive numbered
/// IP/name pair, such as DASHCAM_CAMERA_1_IP and DASHCAM_CAMERA_1_NAME. To add a camera,
/// add the next pair (for example, DASHCAM_CAMERA_3_IP and DASHCAM_CAMERA_3_NAME) under
/// the service's environment section. Do not leave gaps in the numbering.
/// <para>
/// To choose where downloads are stored on the host, change DOWNLOADS_HOST_PATH or the
/// host side of the volume entry below. Compose sets DASHCAM_DOWNLOAD_ROOT to the
/// Synology path /volume1/Data/DashCam; outside Docker, the fallback is /Data/DashCam.
/// </para>
/// <code>
/// volumes:
///   - "${DOWNLOADS_HOST_PATH:-/volume1/Data/DashCam}:/volume1/Data/DashCam"
/// </code>
/// </summary>
internal static class Program
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private static readonly HttpClient HttpClient = new HttpClient()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private static readonly HttpClient CameraProbeHttpClient = new HttpClient()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly HttpClient CameraDeleteHttpClient = new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly string BuildVersion = GetBuildVersion();

    private static readonly string BuildTimeUtc = GetBuildTimeUtc();

    private static string GetBuildVersion()
    {
        System.Reflection.Assembly assembly = typeof(Program).Assembly;
        System.Reflection.AssemblyInformationalVersionAttribute? attribute =
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly);
        if (attribute is null || string.IsNullOrWhiteSpace(attribute.InformationalVersion))
        {
            return "unknown";
        }

        string version = attribute.InformationalVersion;
        int revisionSeparator = version.IndexOf('+');
        if (revisionSeparator >= 0 && version.Length - revisionSeparator - 1 > 7)
        {
            version = version.Substring(0, revisionSeparator + 8);
        }

        return version;
    }

    private static string GetBuildTimeUtc()
    {
        System.Reflection.Assembly assembly = typeof(Program).Assembly;
        foreach (System.Reflection.AssemblyMetadataAttribute metadata in
            System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(assembly))
        {
            if (metadata.Key == "BuildTimestampUtc" && !string.IsNullOrWhiteSpace(metadata.Value))
            {
                return metadata.Value;
            }
        }

        return "unknown";
    }

    public static void Main()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("DASHCAM_DOWNLOAD_ROOT");
        string destinationRoot;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            destinationRoot = "./DashCamDownloads";
        }
        else
        {
            destinationRoot = Path.GetFullPath(configuredRoot);
        }
        Dictionary<string, string> cameras;
        try
        {
            cameras = GetCameras();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        Dictionary<string, CameraProgress> cameraProgress = new Dictionary<string, CameraProgress>();
        foreach (KeyValuePair<string, string> camera in cameras)
        {
            cameraProgress.Add(camera.Key, new CameraProgress(camera.Key, camera.Value));
        }

        CameraSizeLimitStore sizeLimitStore;
        try
        {
            sizeLimitStore = CameraSizeLimitStore.Load(destinationRoot);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        Dictionary<string, SemaphoreSlim> sizeLimitLocks = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        Dictionary<string, CameraScanCoordinator> cameraScans = new Dictionary<string, CameraScanCoordinator>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> camera in cameras)
        {
            cameraProgress[camera.Key].SetMaxSizeGb(
                CameraSizeLimitStore.ConvertBytesToGb(sizeLimitStore.Get(camera.Key)));
            sizeLimitLocks.Add(camera.Key, new SemaphoreSlim(1, 1));
            cameraScans.Add(camera.Key, new CameraScanCoordinator());
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        string? configuredDashboardUrl = Environment.GetEnvironmentVariable("DASHCAM_DASHBOARD_URL");
        if (string.IsNullOrWhiteSpace(configuredDashboardUrl))
        {
            string? configuredPort = Environment.GetEnvironmentVariable("DASHCAM_PORT");
            if (string.IsNullOrWhiteSpace(configuredPort))
            {
                configuredDashboardUrl = "http://127.0.0.1:8081";
            }
            else if (!int.TryParse(
                configuredPort,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int port)
                || port is < 1 or > 65535)
            {
                Console.Error.WriteLine("DASHCAM_PORT must be an integer between 1 and 65535.");
                return;
            }
            else
            {
                configuredDashboardUrl = $"http://+:{port.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        builder.WebHost.UseUrls(configuredDashboardUrl);
        WebApplication app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Context.Response.Headers.Pragma = "no-cache";
                context.Context.Response.Headers.Expires = "0";
            }
        });
        app.MapGet("/api/status", () =>
        {
            foreach (KeyValuePair<string, string> camera in cameras)
            {
                DownloadStatistics statistics = DownloadStatistics.ReadFromDisk(
                    Path.Combine(destinationRoot, camera.Value));
                cameraProgress[camera.Key].SetOnDiskStatistics(statistics.FileCount, statistics.TotalBytes);
                cameraProgress[camera.Key].SetMaxSizeGb(
                    CameraSizeLimitStore.ConvertBytesToGb(sizeLimitStore.Get(camera.Key)));
            }

            List<CameraSnapshot> snapshots = cameraProgress.Values
                .Select(progress => progress.CreateSnapshot())
                .OrderBy(snapshot => snapshot.CameraName, StringComparer.Ordinal)
                .ToList();
            int totalFiles = 0;
            int completedFiles = 0;
            int filesLeft = 0;
            int scanningCameras = 0;
            double aggregateSpeed = 0;
            double overallProgress = 0;

            foreach (CameraSnapshot snapshot in snapshots)
            {
                totalFiles += snapshot.TotalFiles;
                completedFiles += snapshot.CompletedFiles;
                filesLeft += snapshot.FilesLeft;
                if (!snapshot.QueueReady)
                {
                    scanningCameras++;
                }

                aggregateSpeed += snapshot.BytesPerSecond;
                overallProgress += snapshot.OverallProgressPercent * snapshot.TotalFiles;
            }

            if (totalFiles > 0)
            {
                overallProgress /= totalFiles;
            }
            else if (snapshots.Count > 0 && snapshots.All(snapshot => snapshot.TransferStatus == "Complete"))
            {
                overallProgress = 100;
            }

            return Results.Json(new DashboardSnapshot(
                DateTime.UtcNow,
                snapshots.Count,
                totalFiles,
                completedFiles,
                filesLeft,
                aggregateSpeed,
                overallProgress,
                scanningCameras,
                snapshots,
                BuildVersion,
                BuildTimeUtc));
        });
        void QueueCameraScan(string ipAddress)
        {
            cameraScans[ipAddress].RequestScan();
        }

        app.MapPost("/api/cameras/{ipAddress}/scan", async (string ipAddress) =>
        {
            if (!cameras.ContainsKey(ipAddress))
            {
                return Results.NotFound(new { error = "Camera not found." });
            }

            CameraScanCoordinator cameraScan = cameraScans[ipAddress];
            await cameraScan.PauseScanAsync();
            try
            {
                cameraProgress[ipAddress].BeginScan();
            }
            finally
            {
                cameraScan.ResumeScan();
                QueueCameraScan(ipAddress);
            }

            return Results.Accepted(value: new { message = "Camera rescan requested." });
        });
        app.MapGet("/api/cameras/{ipAddress}/files", (string ipAddress, string? path) =>
        {
            if (!cameras.ContainsKey(ipAddress))
            {
                return Results.NotFound(new { error = "Camera not found." });
            }

            if (!TryResolveCameraPath(ipAddress, path ?? string.Empty, isDirectory: true, out Uri? directoryUri)
                || directoryUri is null)
            {
                return Results.BadRequest(new { error = "Invalid camera directory path." });
            }

            try
            {
                List<RemoteEntry> entries = GetEntries(directoryUri);
                string directoryPath = path ?? string.Empty;
                string cameraName = cameras[ipAddress];
                string localRoot = Path.Combine(destinationRoot, cameraName);
                string localDirectory = Path.Combine(
                    new[] { localRoot }.Concat(directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray());
                // Browsing must not rewrite the manifest while a scan may be updating it.
                Dictionary<string, DateTime> downloadedFiles = ReadDownloadedFiles(
                    Path.Combine(localRoot, ".downloaded-files.json"), cameraName, rewrite: false);
                List<CameraFileEntry> files = entries
                    .Select(entry => new CameraFileEntry(
                        entry.Name,
                        directoryPath.Length == 0 ? entry.Name : $"{directoryPath}/{entry.Name}",
                        entry.IsDirectory,
                        entry.FileTime,
                        entry.SizeBytes,
                        entry.SizeIsApproximate,
                        entry.IsDirectory ? null : GetDownloadStatus(entry, localDirectory, downloadedFiles)))
                    .ToList();
                return Results.Json(files);
            }
            catch (Exception ex) when (IsDownloadException(ex))
            {
                Console.Error.WriteLine($"Unable to list files or download status for camera {ipAddress}: {ex.Message}");
                return Results.Problem($"Unable to read the camera directory or download status: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapDelete("/api/cameras/{ipAddress}/files", async (
            string ipAddress,
            [FromBody] CameraFileDeleteRequest? request) =>
        {
            if (!cameras.ContainsKey(ipAddress))
            {
                return Results.NotFound(new { error = "Camera not found." });
            }

            if (request is null
                || string.IsNullOrEmpty(request.Path)
                || !TryResolveCameraPath(ipAddress, request.Path, isDirectory: false, out Uri? fileUri)
                || fileUri is null)
            {
                return Results.BadRequest(new { error = "Invalid camera file path." });
            }

            if (!request.Confirmed)
            {
                return Results.BadRequest(new { error = "Confirm the deletion before deleting a camera file." });
            }

            string[] segments = request.Path.Split('/');
            CameraScanCoordinator cameraScan = cameraScans[ipAddress];
            await cameraScan.PauseScanAsync();
            cameraProgress[ipAddress].PauseForDeletion();
            try
            {
                string fileName = segments[^1];
                string directoryPath = string.Join('/', segments.Take(segments.Length - 1));
                if (!TryResolveCameraPath(ipAddress, directoryPath, isDirectory: true, out Uri? directoryUri)
                    || directoryUri is null)
                {
                    return Results.NotFound(new { error = "Camera file not found." });
                }

                RemoteEntry? fileEntry = GetEntries(directoryUri)
                    .FirstOrDefault(entry => !entry.IsDirectory && entry.Name == fileName);
                if (fileEntry is null)
                {
                    return Results.NotFound(new { error = "Camera file not found." });
                }

                if (fileEntry.CameraFilePath is null)
                {
                    throw new IOException("The camera listing did not include the file's original path.");
                }

                (string? requestError, _) = await SendCameraDeleteRequest(
                    ipAddress, request.Path, fileUri, fileEntry.CameraFilePath);

                bool fileStillExists;
                try
                {
                    fileStillExists = GetEntries(directoryUri)
                        .Any(entry => entry.Name == fileName);
                }
                catch (Exception ex) when (IsDownloadException(ex))
                {
                    Console.Error.WriteLine($"Unable to verify deletion of {request.Path} from {ipAddress}: {ex.Message}");
                    return Results.Problem(
                        $"Deletion could not be verified; the file may still exist. {requestError} {ex.Message}".Trim(),
                        statusCode: StatusCodes.Status502BadGateway);
                }

                if (fileStillExists)
                {
                    return Results.Problem(
                        $"The file is still listed on the camera. {requestError}".Trim(),
                        statusCode: StatusCodes.Status502BadGateway);
                }

                if (requestError is not null)
                {
                    return Results.Problem(
                        $"The file is no longer listed, but the camera did not confirm deletion. {requestError}",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                return Results.Ok(new CameraFileDeleteResponse(request.Path));
            }
            catch (Exception ex) when (IsDownloadException(ex))
            {
                Console.Error.WriteLine($"Unable to delete camera file {request.Path} from {ipAddress}: {ex.Message}");
                return Results.Problem($"Unable to delete the camera file: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
            finally
            {
                cameraScan.ResumeScan();
                QueueCameraScan(ipAddress);
            }
        });
        app.MapPost("/api/cameras/{ipAddress}/files/delete-batch", async (
            string ipAddress,
            [FromBody] CameraFileBatchDeleteRequest? request) =>
        {
            if (!cameras.ContainsKey(ipAddress))
            {
                return Results.NotFound(new { error = "Camera not found." });
            }

            if (request?.Paths is null || request.Paths.Length == 0)
            {
                return Results.BadRequest(new { error = "Select at least one camera file." });
            }

            if (!request.Confirmed)
            {
                return Results.BadRequest(new { error = "Confirm the deletion before deleting camera files." });
            }

            List<(string Path, string Name, Uri FileUri)> files = new List<(string, string, Uri)>();
            HashSet<string> uniquePaths = new HashSet<string>(StringComparer.Ordinal);
            string? directoryPath = null;
            foreach (string path in request.Paths)
            {
                if (string.IsNullOrEmpty(path)
                    || !uniquePaths.Add(path)
                    || !TryResolveCameraPath(ipAddress, path, isDirectory: false, out Uri? fileUri)
                    || fileUri is null)
                {
                    return Results.BadRequest(new { error = "Invalid or duplicate camera file path." });
                }

                string[] segments = path.Split('/');
                string fileDirectoryPath = string.Join('/', segments.Take(segments.Length - 1));
                directoryPath ??= fileDirectoryPath;
                if (!string.Equals(directoryPath, fileDirectoryPath, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { error = "Select files from one camera directory at a time." });
                }

                files.Add((path, segments[^1], fileUri));
            }

            if (!TryResolveCameraPath(ipAddress, directoryPath!, isDirectory: true, out Uri? directoryUri)
                || directoryUri is null)
            {
                return Results.BadRequest(new { error = "Invalid camera directory path." });
            }

            CameraScanCoordinator cameraScan = cameraScans[ipAddress];
            await cameraScan.PauseScanAsync();
            cameraProgress[ipAddress].PauseForDeletion();
            try
            {
                Dictionary<string, RemoteEntry> listedFiles = GetEntries(directoryUri)
                    .Where(entry => !entry.IsDirectory)
                    .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
                Dictionary<string, CameraFileBatchDeleteResult> results =
                    new Dictionary<string, CameraFileBatchDeleteResult>(StringComparer.Ordinal);
                List<(string Path, string Name, string? RequestError)> attempted =
                    new List<(string, string, string?)>();
                bool connectionFailed = false;
                foreach ((string path, string name, Uri fileUri) in files)
                {
                    if (!listedFiles.TryGetValue(name, out RemoteEntry? entry))
                    {
                        results.Add(path, new CameraFileBatchDeleteResult(path, false, "Camera file not found."));
                    }
                    else if (entry.CameraFilePath is null)
                    {
                        results.Add(path, new CameraFileBatchDeleteResult(
                            path, false, "The camera listing did not include the file's original path."));
                    }
                    else if (connectionFailed)
                    {
                        results.Add(path, new CameraFileBatchDeleteResult(
                            path, false, "Not attempted because the camera connection failed."));
                    }
                    else
                    {
                        (string? requestError, bool transportFailure) = await SendCameraDeleteRequest(
                            ipAddress, path, fileUri, entry.CameraFilePath);
                        attempted.Add((path, name, requestError));
                        connectionFailed = transportFailure;
                    }
                }

                HashSet<string>? remainingNames = null;
                string? verificationError = null;
                if (attempted.Count > 0)
                {
                    try
                    {
                        remainingNames = GetEntries(directoryUri)
                            .Select(entry => entry.Name)
                            .ToHashSet(StringComparer.Ordinal);
                    }
                    catch (Exception ex) when (IsDownloadException(ex))
                    {
                        Console.Error.WriteLine($"Unable to verify batch deletion from {ipAddress}: {ex.Message}");
                        verificationError = $"Deletion could not be verified; the file may still exist. {ex.Message}";
                    }
                }

                foreach ((string path, string name, string? requestError) in attempted)
                {
                    string? error;
                    if (verificationError is not null)
                    {
                        error = $"{verificationError} {requestError}".Trim();
                    }
                    else if (remainingNames!.Contains(name))
                    {
                        error = $"The file is still listed on the camera. {requestError}".Trim();
                    }
                    else if (requestError is not null)
                    {
                        error = $"The file is no longer listed, but the camera did not confirm deletion. {requestError}";
                    }
                    else
                    {
                        error = null;
                    }

                    results.Add(path, new CameraFileBatchDeleteResult(path, error is null, error));
                }

                return Results.Ok(new CameraFileBatchDeleteResponse(
                    files.Select(file => results[file.Path]).ToList()));
            }
            catch (Exception ex) when (IsDownloadException(ex))
            {
                Console.Error.WriteLine($"Unable to delete camera files from {ipAddress}: {ex.Message}");
                return Results.Problem($"Unable to delete the camera files: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
            finally
            {
                cameraScan.ResumeScan();
                QueueCameraScan(ipAddress);
            }
        });
        app.MapPut("/api/cameras/{ipAddress}/size-limit", async (string ipAddress, CameraSizeLimitRequest? request) =>
        {
            if (!cameras.TryGetValue(ipAddress, out string? cameraName))
            {
                return Results.NotFound(new { error = "Camera not found." });
            }

            if (request is null
                || !CameraSizeLimitStore.TryConvertGbToBytes(request.MaxSizeGb, out long maxSizeBytes))
            {
                return Results.BadRequest(new { error = "Max size must be a non-negative number of gigabytes." });
            }

            SemaphoreSlim sizeLimitLock = sizeLimitLocks[ipAddress];
            await sizeLimitLock.WaitAsync();
            try
            {
                sizeLimitStore.Set(ipAddress, maxSizeBytes);
                cameraProgress[ipAddress].SetMaxSizeGb(request.MaxSizeGb);
                IReadOnlyList<string> deletedPaths = CameraDiskUsageReader.TrimOldestFiles(
                    Path.Combine(destinationRoot, cameraName),
                    maxSizeBytes);
                cameraProgress[ipAddress].SetStorageCleanupStatus(deletedPaths);
                DownloadStatistics statistics = DownloadStatistics.ReadFromDisk(Path.Combine(destinationRoot, cameraName));
                cameraProgress[ipAddress].SetOnDiskStatistics(statistics.FileCount, statistics.TotalBytes);
                bool limitStillExceeded = maxSizeBytes > 0 && statistics.TotalBytes > maxSizeBytes;
                return Results.Ok(new CameraSizeLimitResponse(request.MaxSizeGb, deletedPaths, limitStillExceeded));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Unable to enforce the size limit for {cameraName}: {ex.Message}");
                return Results.Problem(
                    $"The limit was saved, but cleanup could not be completed: {ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            finally
            {
                sizeLimitLock.Release();
            }
        });

        Task.Run(() => Parallel.ForEach(
            cameras,
            (KeyValuePair<string, string> camera) =>
                DownloadCamera(
                    camera.Key,
                    camera.Value,
                    destinationRoot,
                    cameraProgress[camera.Key],
                    sizeLimitStore,
                    sizeLimitLocks[camera.Key],
                    cameraScans[camera.Key])));

        Console.WriteLine($"DashCamSync version {BuildVersion} (built {BuildTimeUtc})");
        Console.WriteLine($"Starting dashboard on {configuredDashboardUrl}");
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            Console.WriteLine($"Dashboard listening on: {string.Join(", ", app.Urls)}");
        });

        try
        {
            app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Dashboard failed to start on {configuredDashboardUrl}: {ex}");
            throw;
        }
    }

    private static Dictionary<string, string> GetCameras()
    {
        Dictionary<string, string> cameras = new Dictionary<string, string>()
        {
            { "192.168.1.58", "Impreza Dashcam" },
            { "192.168.1.57", "Forrester Dashcam" }
        };
        bool requireCameraSettings = string.Equals(
            Environment.GetEnvironmentVariable("DASHCAM_REQUIRE_CAMERA_SETTINGS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (requireCameraSettings)
        {
            cameras.Clear();
        }

        string? cameraConfiguration = Environment.GetEnvironmentVariable("CAMERAS_JSON");
        if (!string.IsNullOrWhiteSpace(cameraConfiguration))
        {
            Dictionary<string, string>? configuredCameras;
            try
            {
                configuredCameras = JsonSerializer.Deserialize<Dictionary<string, string>>(cameraConfiguration);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("CAMERAS_JSON must be a JSON object mapping IP addresses to camera names.", ex);
            }

            if (configuredCameras is null)
            {
                throw new InvalidOperationException("CAMERAS_JSON must be a JSON object mapping IP addresses to camera names.");
            }

            foreach (KeyValuePair<string, string> camera in configuredCameras)
            {
                if (!IPAddress.TryParse(camera.Key, out IPAddress? parsedAddress)
                    || parsedAddress is null
                    || string.IsNullOrWhiteSpace(camera.Value))
                {
                    throw new InvalidOperationException($"Invalid camera entry in CAMERAS_JSON: '{camera.Key}'. Provide a valid IP address and a non-empty camera name.");
                }

                cameras[camera.Key] = camera.Value;
            }
        }

        bool hasNumberedCameras = false;
        for (int cameraNumber = 1; ; cameraNumber++)
        {
            string? cameraIp = Environment.GetEnvironmentVariable($"DASHCAM_CAMERA_{cameraNumber}_IP");
            string? cameraName = Environment.GetEnvironmentVariable($"DASHCAM_CAMERA_{cameraNumber}_NAME");
            if (string.IsNullOrWhiteSpace(cameraIp) && string.IsNullOrWhiteSpace(cameraName))
            {
                break;
            }

            if (!IPAddress.TryParse(cameraIp, out IPAddress? parsedAddress)
                || parsedAddress is null
                || string.IsNullOrWhiteSpace(cameraName)
                || cameraName.StartsWith("ENTER_CAMERA_", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Replace the ENTER_CAMERA placeholder values for DASHCAM_CAMERA_{cameraNumber}_IP and DASHCAM_CAMERA_{cameraNumber}_NAME with a valid IP address and camera name.");
            }

            if (!hasNumberedCameras)
            {
                cameras.Clear();
                hasNumberedCameras = true;
            }

            cameras[cameraIp] = cameraName;
        }

        if (requireCameraSettings && cameras.Count == 0)
        {
            throw new InvalidOperationException(
                "Camera settings are required in the container. Set DASHCAM_CAMERA_1_IP and DASHCAM_CAMERA_1_NAME, then add more numbered pairs as needed.");
        }

        return cameras;
    }

    private static void DownloadCamera(
        string ipAddress,
        string cameraName,
        string destinationRoot,
        CameraProgress progress,
        CameraSizeLimitStore sizeLimitStore,
        SemaphoreSlim sizeLimitLock,
        CameraScanCoordinator scanCoordinator)
    {
        Uri remoteRoot = new Uri($"http://{ipAddress}/DCIM/");
        string localRoot = Path.Combine(destinationRoot, cameraName);
        progress.Reconnected += scanCoordinator.RequestScan;
        // The monitor keeps running after downloads finish so the dashboard reflects live connectivity.
        Task.Run(() => MonitorCameraConnection(remoteRoot, progress, CancellationToken.None));
        _ = Task.Run(async () =>
        {
            while (true)
            {
                int downloadedFileCount = ScanCamera(
                    remoteRoot,
                    localRoot,
                    cameraName,
                    progress,
                    scanCoordinator,
                    sizeLimitStore,
                    sizeLimitLock);
                if (downloadedFileCount > 0)
                {
                    continue;
                }

                await scanCoordinator.WaitForScanRequestAsync(TimeSpan.FromHours(1));
            }
        });
    }

    private static int ScanCamera(
        Uri remoteRoot,
        string localRoot,
        string cameraName,
        CameraProgress progress,
        CameraScanCoordinator scanCoordinator,
        CameraSizeLimitStore sizeLimitStore,
        SemaphoreSlim sizeLimitLock)
    {
        using CancellationTokenSource? scanCancellation = scanCoordinator.BeginScan();
        if (scanCancellation is null)
        {
            return 0;
        }

        CancellationToken cancellationToken = scanCancellation.Token;
        int completedFileCount = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.BeginScan();
            sizeLimitLock.Wait(cancellationToken);
            try
            {
                IReadOnlyList<string> deletedPaths = CameraDiskUsageReader.TrimOldestFiles(
                    localRoot,
                    sizeLimitStore.Get(progress.IpAddress));
                progress.SetStorageCleanupStatus(deletedPaths);
            }
            finally
            {
                sizeLimitLock.Release();
            }

            CleanUpTemporaryFiles(localRoot, cameraName);
            string manifestPath = Path.Combine(localRoot, ".downloaded-files.json");
            Dictionary<string, DateTime> downloadedFiles = LoadDownloadedFiles(manifestPath, cameraName);
            List<DownloadCandidate> downloads = new List<DownloadCandidate>();
            CollectDownloads(remoteRoot, localRoot, cameraName, downloads, progress, cancellationToken);

            List<DownloadCandidate> prioritizedDownloads = downloads
                .OrderBy(download => download.FileTime)
                .ThenBy(download => download.FileUri.AbsoluteUri, StringComparer.Ordinal)
                .ToList();
            int pendingFileCount = 0;
            foreach (DownloadCandidate download in prioritizedDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!downloadedFiles.ContainsKey(download.FileUri.AbsoluteUri)
                    && !File.Exists(download.DestinationPath))
                {
                    pendingFileCount++;
                }
            }

            progress.SetQueue(pendingFileCount);
            HashSet<string> measuredFiles = MeasureQueueBytes(prioritizedDownloads, downloadedFiles, progress, cancellationToken);

            foreach (DownloadCandidate download in prioritizedDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(download.DestinationPath)!);
                    long? completedLength = DownloadFile(
                        download.FileUri,
                        download.DestinationPath,
                        localRoot,
                        cameraName,
                        download.FileTime,
                        downloadedFiles,
                        manifestPath,
                        progress,
                        sizeLimitStore,
                        sizeLimitLock,
                        cancellationToken);
                    if (completedLength.HasValue)
                    {
                        completedFileCount++;
                        progress.CompleteFile(
                            completedLength.Value,
                            measuredFiles.Contains(download.FileUri.AbsoluteUri));
                    }
                }
                catch (Exception ex) when (IsDownloadException(ex) && !cancellationToken.IsCancellationRequested)
                {
                    progress.MarkError(ex.Message);
                    PrintDownloadFailure(cameraName, download.FileUri, ex);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Finish();
            Console.WriteLine($"Files for {cameraName} are stored in: {localRoot}");
            return completedFileCount;
        }
        catch (Exception ex) when (IsDownloadException(ex) && cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"Interrupted the scan for {cameraName}; it will be restarted.");
            return 0;
        }
        catch (Exception ex) when (IsDownloadException(ex))
        {
            progress.MarkError(ex.Message);
            PrintDownloadFailure(cameraName, remoteRoot, ex);
            return 0;
        }
        finally
        {
            scanCoordinator.EndScan(scanCancellation);
        }
    }

    private static HashSet<string> MeasureQueueBytes(
        List<DownloadCandidate> downloads,
        Dictionary<string, DateTime> downloadedFiles,
        CameraProgress progress,
        CancellationToken cancellationToken)
    {
        HashSet<string> measuredFiles = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        long completedBytes = 0;
        bool incomplete = false;
        foreach (DownloadCandidate download in downloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string downloadKey = download.FileUri.AbsoluteUri;
            if (downloadedFiles.ContainsKey(downloadKey) || File.Exists(download.DestinationPath))
            {
                continue;
            }

            if (incomplete)
            {
                continue;
            }

            long? remoteLength = GetRemoteLength(download.FileUri, cancellationToken);
            if (remoteLength.HasValue)
            {
                totalBytes += remoteLength.Value;
                measuredFiles.Add(downloadKey);
            }
            else
            {
                // One failed probe usually means the camera is unreachable; stop rather than time out per file.
                incomplete = true;
            }

            if (measuredFiles.Count % 25 == 0)
            {
                progress.SetQueueBytes(totalBytes, completedBytes, incomplete);
            }
        }

        progress.SetQueueBytes(totalBytes, completedBytes, incomplete);
        return measuredFiles;
    }

    private static long? GetRemoteLength(Uri fileUri, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, fileUri);
            using HttpResponseMessage response = CameraProbeHttpClient.Send(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.Headers.ContentLength;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && (ex is HttpRequestException || ex is OperationCanceledException || ex is IOException))
        {
            return null;
        }
    }

    private static void MonitorCameraConnection(Uri remoteRoot, CameraProgress progress, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, remoteRoot);
                using HttpResponseMessage response = CameraProbeHttpClient.Send(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                progress.MarkConnected();
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    progress.MarkDisconnected(ex.Message);
                }
            }

            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                return;
            }
        }
    }

    private static void CleanUpTemporaryFiles(string localRoot, string cameraName)
    {
        if (!Directory.Exists(localRoot))
        {
            return;
        }

        List<string> temporaryFiles = new List<string>(
            Directory.EnumerateFiles(localRoot, ".*.tmp", SearchOption.AllDirectories));

        foreach (string temporaryFile in temporaryFiles)
        {
            string temporaryName = Path.GetFileNameWithoutExtension(temporaryFile);
            int guidSeparator = temporaryName.LastIndexOf('.');
            if (guidSeparator < 0
                || !Guid.TryParseExact(temporaryName.Substring(guidSeparator + 1), "N", out Guid _))
            {
                continue;
            }

            File.Delete(temporaryFile);
            Console.WriteLine($"Removed incomplete download for {cameraName}: {temporaryFile}");
        }
    }

    private static void CollectDownloads(
        Uri remoteDirectory,
        string localDirectory,
        string cameraName,
        List<DownloadCandidate> downloads,
        CameraProgress progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localDirectory);
        List<RemoteEntry> entries = GetEntriesWithRetry(remoteDirectory, cameraName, progress, cancellationToken);

        foreach (RemoteEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string localPath = Path.Combine(localDirectory, entry.Name);
            try
            {
                if (entry.IsDirectory)
                {
                    Uri childDirectory = new Uri(entry.Uri.AbsoluteUri.TrimEnd('/') + "/");
                    CollectDownloads(childDirectory, localPath, cameraName, downloads, progress, cancellationToken);
                }
                else if (entry.FileTime is DateTime fileTime)
                {
                    string destinationPath = GetDownloadDestinationPath(localDirectory, entry.Name, fileTime);
                    downloads.Add(new DownloadCandidate(entry.Uri, destinationPath, fileTime));
                }
                else
                {
                    Console.WriteLine($"Skipping {entry.Uri.AbsolutePath}: filetime is missing.");
                }
            }
            catch (Exception ex) when (IsDownloadException(ex) && !cancellationToken.IsCancellationRequested)
            {
                progress.MarkError(ex.Message);
                PrintDownloadFailure(cameraName, entry.Uri, ex);
            }
        }
    }

    private static long? DownloadFile(
        Uri fileUri,
        string destinationPath,
        string localRoot,
        string cameraName,
        DateTime fileTime,
        Dictionary<string, DateTime> downloadedFiles,
        string manifestPath,
        CameraProgress progress,
        CameraSizeLimitStore sizeLimitStore,
        SemaphoreSlim sizeLimitLock,
        CancellationToken cancellationToken)
    {
        string downloadKey = fileUri.AbsoluteUri;
        if (downloadedFiles.ContainsKey(downloadKey))
        {
            if (File.Exists(destinationPath))
            {
                File.SetLastWriteTime(destinationPath, fileTime);
            }

            Console.WriteLine($"Previously downloaded; skipping for {cameraName}: {destinationPath}");
            return null;
        }

        if (File.Exists(destinationPath))
        {
            File.SetLastWriteTime(destinationPath, fileTime);
            SaveDownloadedFile(downloadKey, downloadedFiles, manifestPath);
            Console.WriteLine($"File already exists; skipping for {cameraName}: {destinationPath}");
            return null;
        }

        string partialPath = destinationPath + ".download";
        long partialLength = 0;
        if (File.Exists(partialPath))
        {
            partialLength = new FileInfo(partialPath).Length;
        }

        if (partialLength > 0)
        {
            Console.WriteLine($"Resuming for {cameraName}: {fileUri.AbsolutePath} ({partialLength} bytes already downloaded)");
        }
        else
        {
            Console.WriteLine($"Downloading for {cameraName}: {fileUri.AbsolutePath}");
        }

        progress.BeginFile(fileUri.AbsolutePath, partialLength, null);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DownloadToPartialFile(fileUri, partialPath, cameraName, progress, cancellationToken);
                break;
            }
            catch (Exception ex) when (IsRetryableDownloadException(ex) && !cancellationToken.IsCancellationRequested)
            {
                WaitBeforeRetry(cameraName, fileUri, ex, progress, cancellationToken);
            }
        }

        long completedLength;
        cancellationToken.ThrowIfCancellationRequested();
        sizeLimitLock.Wait(cancellationToken);
        try
        {
            completedLength = new FileInfo(partialPath).Length;
            File.SetLastWriteTime(partialPath, fileTime);
            File.Move(partialPath, destinationPath, true);
            SaveDownloadedFile(downloadKey, downloadedFiles, manifestPath);
            IReadOnlyList<string> deletedPaths = CameraDiskUsageReader.TrimOldestFiles(
                localRoot,
                sizeLimitStore.Get(progress.IpAddress));
            progress.SetStorageCleanupStatus(deletedPaths);
        }
        finally
        {
            sizeLimitLock.Release();
        }

        Console.WriteLine($"Downloaded for {cameraName}: {destinationPath}");
        return completedLength;
    }

    private static string GetDownloadDestinationPath(string localDirectory, string fileName, DateTime fileTime)
    {
        return Path.Combine(localDirectory, fileTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), fileName);
    }

    private static string GetDownloadStatus(
        RemoteEntry entry,
        string localDirectory,
        Dictionary<string, DateTime> downloadedFiles)
    {
        if (entry.FileTime is DateTime fileTime
            && File.Exists(GetDownloadDestinationPath(localDirectory, entry.Name, fileTime)))
        {
            return "downloaded";
        }

        if (downloadedFiles.ContainsKey(entry.Uri.AbsoluteUri))
        {
            return entry.FileTime.HasValue ? "previouslyDownloaded" : "previouslyDownloadedUnknown";
        }

        return entry.FileTime.HasValue ? "notDownloaded" : "unknown";
    }

    private static Dictionary<string, DateTime> LoadDownloadedFiles(string manifestPath, string cameraName)
    {
        return ReadDownloadedFiles(manifestPath, cameraName, rewrite: true);
    }

    private static Dictionary<string, DateTime> ReadDownloadedFiles(string manifestPath, string cameraName, bool rewrite)
    {
        if (!File.Exists(manifestPath))
        {
            return new Dictionary<string, DateTime>(StringComparer.Ordinal);
        }

        Dictionary<string, DateTime> downloadedFiles = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        bool needsRewrite = false;
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.ValueKind == JsonValueKind.Array)
            {
                DateTime migrationTime = DateTime.UtcNow;
                foreach (JsonElement savedFile in manifest.RootElement.EnumerateArray())
                {
                    if (savedFile.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(savedFile.GetString()))
                    {
                        throw new IOException($"Downloaded-file manifest is invalid: {manifestPath}");
                    }

                    downloadedFiles[savedFile.GetString()!] = migrationTime;
                }

                needsRewrite = true;
            }
            else if (manifest.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty savedFile in manifest.RootElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(savedFile.Name)
                        || savedFile.Value.ValueKind != JsonValueKind.String
                        || !savedFile.Value.TryGetDateTime(out DateTime downloadedAtUtc))
                    {
                        throw new IOException($"Downloaded-file manifest is invalid: {manifestPath}");
                    }

                    if (downloadedAtUtc.Kind == DateTimeKind.Unspecified)
                    {
                        downloadedAtUtc = DateTime.SpecifyKind(downloadedAtUtc, DateTimeKind.Utc);
                    }
                    else
                    {
                        downloadedAtUtc = downloadedAtUtc.ToUniversalTime();
                    }

                    downloadedFiles[savedFile.Name] = downloadedAtUtc;
                }
            }
            else
            {
                throw new IOException($"Downloaded-file manifest is invalid: {manifestPath}");
            }
        }
        catch (JsonException ex)
        {
            throw new IOException($"Unable to read downloaded-file manifest: {manifestPath}", ex);
        }

        DateTime expirationTime = DateTime.UtcNow.AddYears(-1);
        List<string> expiredFiles = new List<string>();
        foreach (KeyValuePair<string, DateTime> downloadedFile in downloadedFiles)
        {
            if (downloadedFile.Value <= expirationTime)
            {
                expiredFiles.Add(downloadedFile.Key);
            }
        }

        foreach (string expiredFile in expiredFiles)
        {
            downloadedFiles.Remove(expiredFile);
        }

        if (expiredFiles.Count > 0)
        {
            needsRewrite = true;
            if (rewrite)
            {
                Console.WriteLine($"Removed {expiredFiles.Count} download records older than one year for {cameraName}.");
            }
        }

        if (rewrite && needsRewrite)
        {
            WriteDownloadedFiles(manifestPath, downloadedFiles);
        }

        return downloadedFiles;
    }

    private static void SaveDownloadedFile(
        string downloadKey,
        Dictionary<string, DateTime> downloadedFiles,
        string manifestPath)
    {
        if (downloadedFiles.ContainsKey(downloadKey))
        {
            return;
        }

        Dictionary<string, DateTime> updatedFiles = new Dictionary<string, DateTime>(downloadedFiles, StringComparer.Ordinal)
        {
            [downloadKey] = DateTime.UtcNow
        };
        WriteDownloadedFiles(manifestPath, updatedFiles);
        downloadedFiles[downloadKey] = updatedFiles[downloadKey];
    }

    private static void WriteDownloadedFiles(string manifestPath, Dictionary<string, DateTime> downloadedFiles)
    {
        Dictionary<string, DateTime> orderedFiles = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, DateTime> downloadedFile in downloadedFiles.OrderBy(
            downloadedFile => downloadedFile.Key,
            StringComparer.Ordinal))
        {
            orderedFiles.Add(downloadedFile.Key, downloadedFile.Value);
        }

        string temporaryManifestPath = manifestPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryManifestPath, JsonSerializer.Serialize(orderedFiles));
            File.Move(temporaryManifestPath, manifestPath, true);
        }
        finally
        {
            if (File.Exists(temporaryManifestPath))
            {
                File.Delete(temporaryManifestPath);
            }
        }
    }

    private static void DownloadToPartialFile(
        Uri fileUri,
        string partialPath,
        string cameraName,
        CameraProgress progress,
        CancellationToken cancellationToken)
    {
        long partialLength = 0;
        if (File.Exists(partialPath))
        {
            partialLength = new FileInfo(partialPath).Length;
        }

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, fileUri);
        if (partialLength > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(partialLength, null);
        }

        using HttpResponseMessage response = HttpClient.Send(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        progress.MarkConnected();
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && partialLength > 0)
        {
            System.Net.Http.Headers.ContentRangeHeaderValue? contentRange = response.Content.Headers.ContentRange;
            if (contentRange is not null && contentRange.Length == partialLength)
            {
                return;
            }

            File.Delete(partialPath);
            Console.WriteLine($"Restarting download because the remote file size changed: {fileUri.AbsolutePath}");
            DownloadToPartialFile(fileUri, partialPath, cameraName, progress, cancellationToken);
            return;
        }

        bool appendToPartial = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        long? expectedLength;
        if (appendToPartial)
        {
            System.Net.Http.Headers.ContentRangeHeaderValue? contentRange = response.Content.Headers.ContentRange;
            if (partialLength == 0 || contentRange is null || contentRange.From != partialLength)
            {
                throw new IOException($"The server returned an unexpected byte range for {fileUri.AbsolutePath}.");
            }

            expectedLength = contentRange.Length;
            if (!expectedLength.HasValue && response.Content.Headers.ContentLength.HasValue)
            {
                expectedLength = partialLength + response.Content.Headers.ContentLength.Value;
            }
        }
        else
        {
            response.EnsureSuccessStatusCode();
            expectedLength = response.Content.Headers.ContentLength;
        }

        using Stream source = response.Content.ReadAsStream();
        FileMode fileMode;
        if (appendToPartial)
        {
            fileMode = FileMode.Append;
        }
        else
        {
            fileMode = FileMode.Create;
        }

        using FileStream destination = new FileStream(partialPath, fileMode, FileAccess.Write);
        CopyResponseToFile(source, destination, fileUri, cameraName, expectedLength, progress, cancellationToken);
        destination.Flush();

        if (expectedLength.HasValue && destination.Length != expectedLength.Value)
        {
            IOException innerException = new IOException(
                $"Received {destination.Length} of {expectedLength.Value} expected bytes.");
            throw new DownloadInterruptedException(fileUri, innerException);
        }
    }

    private static void CopyResponseToFile(
        Stream source,
        Stream destination,
        Uri fileUri,
        string cameraName,
        long? totalLength,
        CameraProgress progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 1024];
        Stopwatch progressTimer = Stopwatch.StartNew();
        long startingLength = destination.Length;
        TimeSpan lastProgressReport = TimeSpan.Zero;
        int nextPercentageToReport = 5;
        if (totalLength.HasValue && totalLength.Value > 0)
        {
            int initialPercentage = (int)((double)destination.Length * 100d / totalLength.Value);
            nextPercentageToReport = (initialPercentage / 5 * 5) + 5;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead;
            try
            {
                // A camera that drops off the network otherwise leaves this read blocked indefinitely.
                using CancellationTokenSource readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                bytesRead = source.ReadAsync(buffer.AsMemory(), readTimeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                && (ex is IOException || ex is HttpRequestException || ex is OperationCanceledException))
            {
                throw new DownloadInterruptedException(fileUri, ex);
            }

            if (bytesRead == 0)
            {
                return;
            }

            destination.Write(buffer, 0, bytesRead);

            double currentSpeed = 0;
            if (progressTimer.Elapsed.TotalSeconds > 0)
            {
                currentSpeed = (destination.Length - startingLength) / progressTimer.Elapsed.TotalSeconds;
            }

            progress.UpdateFileProgress(destination.Length, totalLength, currentSpeed);

            if (totalLength.HasValue && totalLength.Value > 0)
            {
                int percentage = (int)Math.Min(100d, (double)destination.Length * 100d / totalLength.Value);
                if (percentage >= nextPercentageToReport || destination.Length >= totalLength.Value)
                {
                    Console.WriteLine(
                        $"Progress for {cameraName}: {fileUri.AbsolutePath} {percentage}% ({destination.Length:N0}/{totalLength.Value:N0} bytes)");
                    nextPercentageToReport = (percentage / 5 * 5) + 5;
                }
            }
            else if (progressTimer.Elapsed - lastProgressReport >= TimeSpan.FromSeconds(1))
            {
                Console.WriteLine(
                    $"Progress for {cameraName}: {fileUri.AbsolutePath} ({destination.Length:N0} bytes)");
                lastProgressReport = progressTimer.Elapsed;
            }
        }
    }

    private static List<RemoteEntry> GetEntriesWithRetry(
        Uri remoteDirectory,
        string cameraName,
        CameraProgress progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return GetEntries(remoteDirectory, progress, cancellationToken);
            }
            catch (Exception ex) when (IsRetryableDownloadException(ex) && !cancellationToken.IsCancellationRequested)
            {
                WaitBeforeRetry(cameraName, remoteDirectory, ex, progress, cancellationToken);
            }
        }
    }

    private static List<RemoteEntry> GetEntries(
        Uri remoteDirectory,
        CameraProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        UriBuilder cameraRootBuilder = new UriBuilder(remoteDirectory)
        {
            Path = "/DCIM/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        Uri cameraRoot = cameraRootBuilder.Uri;
        if (!cameraRoot.IsBaseOf(remoteDirectory))
        {
            throw new IOException("The camera directory is outside the DCIM folder.");
        }

        Uri listingUri = new UriBuilder(remoteDirectory)
        {
            Path = "/",
            Query = "custom=1&cmd=3015",
            Fragment = string.Empty
        }.Uri;
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, listingUri);
        using HttpResponseMessage response = HttpClient.Send(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        progress?.MarkConnected();

        using StreamReader reader = new StreamReader(response.Content.ReadAsStream());
        string xml;
        try
        {
            xml = reader.ReadToEndAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && (ex is IOException || ex is HttpRequestException || ex is OperationCanceledException))
        {
            throw new DownloadInterruptedException(listingUri, ex);
        }

        XDocument listing;
        try
        {
            using StringReader stringReader = new StringReader(xml);
            using XmlReader xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            listing = XDocument.Load(xmlReader);
        }
        catch (XmlException ex)
        {
            throw new IOException("The camera returned an invalid XML file listing.", ex);
        }

        if (listing.Root?.Name != "LIST")
        {
            throw new IOException("The camera returned an unexpected XML file listing.");
        }

        string relativeDirectory = Uri.UnescapeDataString(
            cameraRoot.MakeRelativeUri(remoteDirectory).ToString()).TrimEnd('/');
        string[] directorySegments = relativeDirectory.Length == 0
            ? Array.Empty<string>()
            : relativeDirectory.Split('/');
        Dictionary<string, RemoteEntry> entries = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
        Dictionary<string, XElement> fileRecords = new Dictionary<string, XElement>(StringComparer.Ordinal);
        string? cameraFilePathPrefix = null;

        foreach (XElement file in listing.Root.Descendants("File"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = file.Element("NAME")?.Value.Trim() ?? string.Empty;
            string filePath = file.Element("FPATH")?.Value.Trim() ?? string.Empty;
            string[] fileSegments = GetCameraRelativeFileSegments(filePath);
            cameraFilePathPrefix ??= GetCameraFilePathPrefix(filePath);
            if (name.Length == 0 || !string.Equals(name, fileSegments[^1], StringComparison.Ordinal))
            {
                throw new IOException("The camera returned a file with an invalid name or path.");
            }

            if (fileSegments.Length <= directorySegments.Length
                || !directorySegments.SequenceEqual(
                    fileSegments.Take(directorySegments.Length),
                    StringComparer.Ordinal))
            {
                continue;
            }

            int childSegmentCount = directorySegments.Length + 1;
            string childName = fileSegments[directorySegments.Length];
            bool isDirectory = fileSegments.Length > childSegmentCount;
            string childPath = string.Join('/', fileSegments
                .Take(childSegmentCount)
                .Select(Uri.EscapeDataString));
            Uri entryUri = new Uri(cameraRoot, childPath + (isDirectory ? "/" : string.Empty));
            DateTime? fileTime = null;
            if (!isDirectory
                && DateTime.TryParseExact(
                    file.Element("TIME")?.Value.Trim(),
                    "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsedFileTime))
            {
                fileTime = parsedFileTime;
            }

            long? sizeBytes = null;
            if (!isDirectory
                && long.TryParse(
                    file.Element("SIZE")?.Value.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long parsedSize)
                && parsedSize >= 0)
            {
                sizeBytes = parsedSize;
            }

            RemoteEntry entry = new RemoteEntry(
                childName,
                entryUri,
                isDirectory,
                fileTime,
                isDirectory ? null : filePath,
                sizeBytes,
                false);
            if (entries.TryGetValue(childName, out RemoteEntry? existingEntry))
            {
                // Some cameras repeat identical file records; conflicting records remain unsafe.
                if ((isDirectory && existingEntry.IsDirectory)
                    || (!isDirectory && !existingEntry.IsDirectory
                        && fileRecords.TryGetValue(childName, out XElement? existingRecord)
                        && XNode.DeepEquals(existingRecord, file)))
                {
                    continue;
                }

                throw new IOException("The camera returned duplicate or conflicting file paths.");
            }

            entries.Add(childName, entry);
            if (!isDirectory)
            {
                fileRecords.Add(childName, file);
            }
        }

        // The XML listing can omit photos that are present in the HTML directory listing.
        foreach (RemoteEntry htmlEntry in GetHtmlEntries(remoteDirectory, cancellationToken))
        {
            if (entries.TryGetValue(htmlEntry.Name, out RemoteEntry? xmlEntry))
            {
                if (xmlEntry.IsDirectory != htmlEntry.IsDirectory)
                {
                    throw new IOException("The camera returned conflicting file and directory listings.");
                }

                if (!xmlEntry.IsDirectory && !xmlEntry.SizeBytes.HasValue)
                {
                    entries[htmlEntry.Name] = xmlEntry with
                    {
                        SizeBytes = htmlEntry.SizeBytes,
                        SizeIsApproximate = htmlEntry.SizeIsApproximate
                    };
                }

                continue;
            }

            string? cameraFilePath = htmlEntry.IsDirectory || cameraFilePathPrefix is null
                ? null
                : cameraFilePathPrefix + Uri.UnescapeDataString(
                    cameraRoot.MakeRelativeUri(htmlEntry.Uri).ToString()).Replace('/', '\\');
            entries.Add(htmlEntry.Name, htmlEntry with { CameraFilePath = cameraFilePath });
        }

        return entries.Values
            .OrderBy(entry => !entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<RemoteEntry> GetHtmlEntries(Uri remoteDirectory, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, remoteDirectory);
        using HttpResponseMessage response = HttpClient.Send(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using StreamReader reader = new StreamReader(response.Content.ReadAsStream());
        string html;
        try
        {
            html = reader.ReadToEndAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && (ex is IOException || ex is HttpRequestException || ex is OperationCanceledException))
        {
            throw new DownloadInterruptedException(remoteDirectory, ex);
        }

        if (!Regex.IsMatch(html, "<table\\b[^>]*>", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(html, "</table\\s*>", RegexOptions.IgnoreCase))
        {
            throw new IOException("The camera returned an invalid directory listing.");
        }

        List<RemoteEntry> entries = new List<RemoteEntry>();
        Regex rowPattern = new Regex(
            "<tr\\b(?<row>.*?)(?=<tr\\b|</table>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Regex linkPattern = new Regex(
            "<a\\b[^>]*\\bhref\\s*=\\s*[\\\"'](?<href>[^\\\"']+)[\\\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Regex fileTimePattern = new Regex("(?<filetime>\\d{4}/\\d{2}/\\d{2}\\s+\\d{2}:\\d{2}:\\d{2})");
        Regex fileSizePattern = new Regex(
            "</a>\\s*<td\\b[^>]*>\\s*(?<size>\\d+(?:\\.\\d+)?)\\s*(?<unit>B|KB|MB|GB|TB)\\s*<td\\b",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match rowMatch in rowPattern.Matches(html))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string row = rowMatch.Groups["row"].Value;
            Match linkMatch = linkPattern.Match(row);
            if (!linkMatch.Success)
            {
                continue;
            }

            string href = WebUtility.HtmlDecode(linkMatch.Groups["href"].Value);
            if (!Uri.TryCreate(remoteDirectory, href, out Uri? entryUri)
                || entryUri is null
                || !remoteDirectory.IsBaseOf(entryUri)
                || entryUri.Query.Length > 0)
            {
                continue;
            }

            string name = Uri.UnescapeDataString(remoteDirectory.MakeRelativeUri(entryUri).ToString()).TrimEnd('/');
            if (name.Length == 0 || name.Contains('/'))
            {
                continue;
            }

            bool isDirectory = Regex.IsMatch(row, "<i>\\s*folder\\s*</i>", RegexOptions.IgnoreCase);
            DateTime? fileTime = null;
            Match fileTimeMatch = fileTimePattern.Match(row);
            if (fileTimeMatch.Success
                && DateTime.TryParseExact(
                    fileTimeMatch.Groups["filetime"].Value,
                    "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsedFileTime))
            {
                fileTime = parsedFileTime;
            }

            long? sizeBytes = null;
            Match fileSizeMatch = fileSizePattern.Match(row);
            if (!isDirectory && fileSizeMatch.Success
                && decimal.TryParse(
                    fileSizeMatch.Groups["size"].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out decimal size))
            {
                decimal multiplier = fileSizeMatch.Groups["unit"].Value.ToUpperInvariant() switch
                {
                    "KB" => 1024m,
                    "MB" => 1024m * 1024,
                    "GB" => 1024m * 1024 * 1024,
                    "TB" => 1024m * 1024 * 1024 * 1024,
                    _ => 1m
                };
                if (size <= long.MaxValue / multiplier)
                {
                    sizeBytes = (long)decimal.Round(size * multiplier);
                }
            }

            entries.Add(new RemoteEntry(name, entryUri, isDirectory, fileTime, null, sizeBytes, sizeBytes.HasValue));
        }

        return entries;
    }

    private static string? GetCameraFilePathPrefix(string filePath)
    {
        string[] pathSegments = filePath.Replace('/', '\\').Split('\\');
        int dcimIndex = Array.FindIndex(pathSegments, segment =>
            string.Equals(segment, "DCIM", StringComparison.OrdinalIgnoreCase));
        if (dcimIndex < 0)
        {
            return null;
        }

        return string.Join("\\", pathSegments.Take(dcimIndex + 1)) + "\\";
    }

    private static string[] GetCameraRelativeFileSegments(string filePath)
    {
        string[] pathSegments = filePath.Replace('\\', '/').Split('/');
        int dcimIndex = Array.FindIndex(pathSegments, segment =>
            string.Equals(segment, "DCIM", StringComparison.OrdinalIgnoreCase));
        if (dcimIndex < 0 || dcimIndex == pathSegments.Length - 1)
        {
            throw new IOException("The camera returned a file path outside the DCIM folder.");
        }

        string[] relativeSegments = pathSegments.Skip(dcimIndex + 1).ToArray();
        if (relativeSegments.Any(segment =>
            segment.Length == 0
            || segment is "." or ".."
            || segment.Any(char.IsControl)))
        {
            throw new IOException("The camera returned an invalid file path.");
        }

        return relativeSegments;
    }

    private static async Task<(string? Error, bool TransportFailure)> SendCameraDeleteRequest(
        string ipAddress,
        string path,
        Uri fileUri,
        string cameraFilePath)
    {
        UriBuilder deleteUri = new UriBuilder(fileUri)
        {
            Path = "/",
            Query = $"custom=1&cmd=4003&str={Uri.EscapeDataString(cameraFilePath)}"
        };
        try
        {
            using HttpRequestMessage deleteRequest = new HttpRequestMessage(HttpMethod.Get, deleteUri.Uri);
            using HttpResponseMessage response = CameraDeleteHttpClient.Send(deleteRequest);
            if (!response.IsSuccessStatusCode)
            {
                return ($"The camera rejected the delete request ({(int)response.StatusCode}).", false);
            }

            string responseXml = await response.Content.ReadAsStringAsync();
            if (!HasSuccessfulCameraDeleteStatus(responseXml))
            {
                return ("The camera did not confirm deletion with <Status>0</Status>.", false);
            }

            return (null, false);
        }
        catch (Exception ex) when (IsDownloadException(ex))
        {
            Console.Error.WriteLine($"Camera delete request for {path} from {ipAddress} failed: {ex.Message}");
            return ($"The delete request failed: {ex.Message}", ex is HttpRequestException || ex is OperationCanceledException);
        }
    }

    private static bool HasSuccessfulCameraDeleteStatus(string xml)
    {
        XDocument response;
        try
        {
            using StringReader stringReader = new StringReader(xml);
            using XmlReader xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            response = XDocument.Load(xmlReader);
        }
        catch (XmlException ex)
        {
            throw new IOException("The camera returned an invalid XML delete response.", ex);
        }

        XElement? status = response.Root?.Name.LocalName == "Status"
            ? response.Root
            : response.Descendants().FirstOrDefault(element => element.Name.LocalName == "Status");
        return status?.Value.Trim() == "0";
    }

    private static bool TryResolveCameraPath(
        string ipAddress,
        string relativePath,
        bool isDirectory,
        out Uri? resolvedUri)
    {
        resolvedUri = null;
        if (relativePath.StartsWith('/') || relativePath.Contains('\\')
            || (!isDirectory && relativePath.EndsWith('/')))
        {
            return false;
        }

        string[] segments = relativePath.Length == 0 ? Array.Empty<string>() : relativePath.Split('/');
        if (segments.Any(segment =>
        {
            if (segment.Length == 0)
            {
                return true;
            }

            string decodedSegment = segment;
            for (int decodePass = 0; decodePass < 3; decodePass++)
            {
                decodedSegment = Uri.UnescapeDataString(decodedSegment);
                if (decodedSegment is "." or ".."
                    || decodedSegment.Contains('/')
                    || decodedSegment.Contains('\\')
                    || decodedSegment.Any(char.IsControl))
                {
                    return true;
                }
            }

            return false;
        }))
        {
            return false;
        }

        if (!isDirectory && segments.Length == 0)
        {
            return false;
        }

        Uri cameraRoot = new Uri($"http://{ipAddress}/DCIM/");
        string encodedPath = string.Join('/', segments.Select(Uri.EscapeDataString));
        if (isDirectory && encodedPath.Length > 0)
        {
            encodedPath += "/";
        }

        Uri candidate = new Uri(cameraRoot, encodedPath);
        if (!cameraRoot.IsBaseOf(candidate) || candidate.Query.Length > 0)
        {
            return false;
        }

        resolvedUri = candidate;
        return true;
    }

    private static bool IsDownloadException(Exception exception)
    {
        return exception is HttpRequestException
            || exception is OperationCanceledException
            || exception is IOException
            || exception is UnauthorizedAccessException;
    }

    private static bool IsRetryableDownloadException(Exception exception)
    {
        if (exception is HttpRequestException httpException)
        {
            if (!httpException.StatusCode.HasValue)
            {
                return true;
            }

            int statusCode = (int)httpException.StatusCode.Value;
            return httpException.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                || httpException.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || statusCode >= 500;
        }

        return exception is OperationCanceledException || exception is DownloadInterruptedException;
    }

    private static void WaitBeforeRetry(
        string cameraName,
        Uri uri,
        Exception exception,
        CameraProgress progress,
        CancellationToken cancellationToken)
    {
        progress.MarkRetrying(exception.Message);
        Console.Error.WriteLine(
            $"Connection interrupted for {cameraName} at {uri.AbsolutePath}: {exception.Message} Retrying in 10 seconds.");
        if (cancellationToken.WaitHandle.WaitOne(RetryDelay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void PrintDownloadFailure(string cameraName, Uri uri, Exception exception)
    {
        Console.WriteLine($"Unable to process {uri.AbsolutePath} for {cameraName}: {exception.Message}");
    }

    private sealed class CameraScanCoordinator
    {
        private readonly object syncRoot = new object();
        private readonly SemaphoreSlim scanLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim pauseLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim scanRequestSignal = new SemaphoreSlim(0, 1);
        private CancellationTokenSource? activeScan;
        private bool pauseRequested;

        public CancellationTokenSource? BeginScan()
        {
            scanLock.Wait();
            lock (syncRoot)
            {
                if (pauseRequested)
                {
                    scanLock.Release();
                    return null;
                }

                activeScan = new CancellationTokenSource();
                return activeScan;
            }
        }

        public void EndScan(CancellationTokenSource scan)
        {
            lock (syncRoot)
            {
                if (activeScan == scan)
                {
                    activeScan = null;
                }
            }

            scanLock.Release();
        }

        public async Task PauseScanAsync()
        {
            // Serialize pause requests and wait for the cancelled scan to release the camera.
            await pauseLock.WaitAsync();
            lock (syncRoot)
            {
                pauseRequested = true;
                activeScan?.Cancel();
            }

            await scanLock.WaitAsync();
        }

        public void ResumeScan()
        {
            lock (syncRoot)
            {
                pauseRequested = false;
            }

            scanLock.Release();
            pauseLock.Release();
        }

        public void RequestScan()
        {
            lock (syncRoot)
            {
                if (scanRequestSignal.CurrentCount == 0)
                {
                    scanRequestSignal.Release();
                }
            }
        }

        public Task<bool> WaitForScanRequestAsync(TimeSpan timeout)
        {
            return scanRequestSignal.WaitAsync(timeout);
        }
    }

    private sealed record RemoteEntry(
        string Name,
        Uri Uri,
        bool IsDirectory,
        DateTime? FileTime,
        string? CameraFilePath,
        long? SizeBytes,
        bool SizeIsApproximate);

    private sealed record DownloadCandidate(Uri FileUri, string DestinationPath, DateTime FileTime);

    private sealed record DashboardSnapshot(
        DateTime UpdatedAtUtc,
        int CameraCount,
        int TotalFiles,
        int CompletedFiles,
        int FilesLeft,
        double AggregateBytesPerSecond,
        double OverallProgressPercent,
        int ScanningCameras,
        IReadOnlyList<CameraSnapshot> Cameras,
        string Version,
        string BuildTimeUtc);

    private sealed record CameraSizeLimitRequest(decimal MaxSizeGb);

    private sealed record CameraSizeLimitResponse(decimal MaxSizeGb, IReadOnlyList<string> DeletedPaths, bool LimitStillExceeded);

    private sealed record CameraFileEntry(
        string Name,
        string Path,
        bool IsDirectory,
        DateTime? FileTime,
        long? SizeBytes,
        bool SizeIsApproximate,
        string? DownloadStatus);

    private sealed record CameraFileDeleteRequest(string Path, bool Confirmed);

    private sealed record CameraFileDeleteResponse(string DeletedPath);

    private sealed record CameraFileBatchDeleteRequest(string[]? Paths, bool Confirmed);

    private sealed record CameraFileBatchDeleteResult(string Path, bool Deleted, string? Error);

    private sealed record CameraFileBatchDeleteResponse(IReadOnlyList<CameraFileBatchDeleteResult> Results);

    private sealed class DownloadInterruptedException : IOException
    {
        public DownloadInterruptedException(Uri uri, Exception innerException)
            : base($"Transfer interrupted for {uri.AbsolutePath}.", innerException)
        {
        }
    }
}
