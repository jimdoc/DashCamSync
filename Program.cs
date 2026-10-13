using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private static readonly HttpClient HttpClient = new HttpClient()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private static readonly HttpClient CameraProbeHttpClient = new HttpClient()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

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

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        string? configuredDashboardUrl = Environment.GetEnvironmentVariable("DASHCAM_DASHBOARD_URL");
        if (string.IsNullOrWhiteSpace(configuredDashboardUrl))
        {
            configuredDashboardUrl = "http://127.0.0.1:8081";
        }

        builder.WebHost.UseUrls(configuredDashboardUrl);
        WebApplication app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/status", () =>
        {
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
                snapshots));
        });

        Task.Run(() => Parallel.ForEach(
            cameras,
            (KeyValuePair<string, string> camera) =>
                DownloadCamera(camera.Key, camera.Value, destinationRoot, cameraProgress[camera.Key])));

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
        CameraProgress progress)
    {
        Uri remoteRoot = new Uri($"http://{ipAddress}/DCIM/");
        string localRoot = Path.Combine(destinationRoot, cameraName);
        CancellationTokenSource connectionMonitorCancellation = new CancellationTokenSource();
        Task connectionMonitor = Task.Run(() => MonitorCameraConnection(
            remoteRoot,
            progress,
            connectionMonitorCancellation.Token));

        try
        {
            progress.SetTransferStatus("Scanning");
            CleanUpTemporaryFiles(localRoot, cameraName);
            string manifestPath = Path.Combine(localRoot, ".downloaded-files.json");
            Dictionary<string, DateTime> downloadedFiles = LoadDownloadedFiles(manifestPath, cameraName);
            List<DownloadCandidate> downloads = new List<DownloadCandidate>();
            CollectDownloads(remoteRoot, localRoot, cameraName, downloads, progress);

            List<DownloadCandidate> oldestFirstDownloads = downloads
                .OrderBy(download => download.FileTime)
                .ThenBy(download => download.FileUri.AbsoluteUri, StringComparer.Ordinal)
                .ToList();
            int alreadyCompleteCount = 0;
            foreach (DownloadCandidate download in oldestFirstDownloads)
            {
                if (downloadedFiles.ContainsKey(download.FileUri.AbsoluteUri)
                    || File.Exists(download.DestinationPath))
                {
                    alreadyCompleteCount++;
                }
            }

            progress.SetQueue(oldestFirstDownloads.Count, alreadyCompleteCount);

            foreach (DownloadCandidate download in oldestFirstDownloads)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(download.DestinationPath)!);
                    bool wasAlreadyComplete = downloadedFiles.ContainsKey(download.FileUri.AbsoluteUri)
                        || File.Exists(download.DestinationPath);
                    DownloadFile(
                        download.FileUri,
                        download.DestinationPath,
                        cameraName,
                        download.FileTime,
                        downloadedFiles,
                        manifestPath,
                        progress);
                    if (!wasAlreadyComplete)
                    {
                        progress.CompleteFile();
                    }
                }
                catch (Exception ex) when (IsDownloadException(ex))
                {
                    progress.MarkError(ex.Message);
                    PrintDownloadFailure(cameraName, download.FileUri, ex);
                }
            }

            progress.Finish();
            Console.WriteLine($"Files for {cameraName} are stored in: {localRoot}");
        }
        catch (Exception ex) when (IsDownloadException(ex))
        {
            progress.MarkError(ex.Message);
            PrintDownloadFailure(cameraName, remoteRoot, ex);
        }
        finally
        {
            connectionMonitorCancellation.Cancel();
            connectionMonitor.Wait();
            connectionMonitorCancellation.Dispose();
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
                progress.MarkConnected();
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                || ex is OperationCanceledException
                || ex is IOException)
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
        CameraProgress progress)
    {
        Directory.CreateDirectory(localDirectory);
        List<RemoteEntry> entries = GetEntriesWithRetry(remoteDirectory, cameraName, progress);

        foreach (RemoteEntry entry in entries)
        {
            string localPath = Path.Combine(localDirectory, entry.Name);
            try
            {
                if (entry.IsDirectory)
                {
                    Uri childDirectory = new Uri(entry.Uri.AbsoluteUri.TrimEnd('/') + "/");
                    CollectDownloads(childDirectory, localPath, cameraName, downloads, progress);
                }
                else if (entry.FileTime is DateTime fileTime)
                {
                    string dateDirectoryName = fileTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    string dateDirectory = Path.Combine(localDirectory, dateDirectoryName);
                    string destinationPath = Path.Combine(dateDirectory, entry.Name);
                    downloads.Add(new DownloadCandidate(entry.Uri, destinationPath, fileTime));
                }
                else
                {
                    Console.WriteLine($"Skipping {entry.Uri.AbsolutePath}: filetime is missing.");
                }
            }
            catch (Exception ex) when (IsDownloadException(ex))
            {
                progress.MarkError(ex.Message);
                PrintDownloadFailure(cameraName, entry.Uri, ex);
            }
        }
    }

    private static void DownloadFile(
        Uri fileUri,
        string destinationPath,
        string cameraName,
        DateTime fileTime,
        Dictionary<string, DateTime> downloadedFiles,
        string manifestPath,
        CameraProgress progress)
    {
        string downloadKey = fileUri.AbsoluteUri;
        if (downloadedFiles.ContainsKey(downloadKey))
        {
            if (File.Exists(destinationPath))
            {
                File.SetLastWriteTime(destinationPath, fileTime);
            }

            Console.WriteLine($"Previously downloaded; skipping for {cameraName}: {destinationPath}");
            return;
        }

        if (File.Exists(destinationPath))
        {
            File.SetLastWriteTime(destinationPath, fileTime);
            Console.WriteLine($"Already downloaded for {cameraName}: {destinationPath}");
            return;
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
            try
            {
                DownloadToPartialFile(fileUri, partialPath, cameraName, progress);
                break;
            }
            catch (Exception ex) when (IsRetryableDownloadException(ex))
            {
                WaitBeforeRetry(cameraName, fileUri, ex, progress);
            }
        }

        File.SetLastWriteTime(partialPath, fileTime);
        File.Move(partialPath, destinationPath);
        SaveDownloadedFile(downloadKey, downloadedFiles, manifestPath);
        Console.WriteLine($"Downloaded for {cameraName}: {destinationPath}");
    }

    private static Dictionary<string, DateTime> LoadDownloadedFiles(string manifestPath, string cameraName)
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
            Console.WriteLine($"Removed {expiredFiles.Count} download records older than one year for {cameraName}.");
        }

        if (needsRewrite)
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
        CameraProgress progress)
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

        using HttpResponseMessage response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
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
            DownloadToPartialFile(fileUri, partialPath, cameraName, progress);
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
        CopyResponseToFile(source, destination, fileUri, cameraName, expectedLength, progress);
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
        CameraProgress progress)
    {
        byte[] buffer = new byte[81920];
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
            int bytesRead;
            try
            {
                bytesRead = source.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is OperationCanceledException)
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
        CameraProgress progress)
    {
        while (true)
        {
            try
            {
                return GetEntries(remoteDirectory, progress);
            }
            catch (Exception ex) when (IsRetryableDownloadException(ex))
            {
                WaitBeforeRetry(cameraName, remoteDirectory, ex, progress);
            }
        }
    }

    private static List<RemoteEntry> GetEntries(Uri remoteDirectory, CameraProgress progress)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, remoteDirectory);
        using HttpResponseMessage response = HttpClient.Send(request);
        response.EnsureSuccessStatusCode();
        progress.MarkConnected();

        using StreamReader reader = new StreamReader(response.Content.ReadAsStream());
        string html;
        try
        {
            html = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is OperationCanceledException)
        {
            throw new DownloadInterruptedException(remoteDirectory, ex);
        }

        List<RemoteEntry> entries = new List<RemoteEntry>();
        Regex rowPattern = new Regex("<tr\\b(?<row>.*?)(?=<tr\\b|</table>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Regex linkPattern = new Regex("<a\\b[^>]*\\bhref\\s*=\\s*[\\\"'](?<href>[^\\\"']+)[\\\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Regex fileTimePattern = new Regex("(?<filetime>\\d{4}/\\d{2}/\\d{2}\\s+\\d{2}:\\d{2}:\\d{2})");

        foreach (Match rowMatch in rowPattern.Matches(html))
        {
            string row = rowMatch.Groups["row"].Value;
            Match linkMatch = linkPattern.Match(row);
            if (!linkMatch.Success)
            {
                continue;
            }

            string href = System.Net.WebUtility.HtmlDecode(linkMatch.Groups["href"].Value);
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
            if (!isDirectory)
            {
                Match fileTimeMatch = fileTimePattern.Match(row);
                string fileTimeText;
                if (fileTimeMatch.Success)
                {
                    fileTimeText = fileTimeMatch.Groups["filetime"].Value;
                }
                else
                {
                    fileTimeText = string.Empty;
                }

                if (!DateTime.TryParseExact(
                    fileTimeText,
                    "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsedFileTime))
                {
                    Console.WriteLine($"Skipping {entryUri.AbsolutePath}: invalid filetime '{fileTimeText}'.");
                    continue;
                }

                fileTime = parsedFileTime;
            }

            entries.Add(new RemoteEntry(name, entryUri, isDirectory, fileTime));
        }

        return entries;
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
        CameraProgress progress)
    {
        progress.MarkRetrying(exception.Message);
        Console.Error.WriteLine(
            $"Connection interrupted for {cameraName} at {uri.AbsolutePath}: {exception.Message} Retrying in 5 minutes.");
        Thread.Sleep(RetryDelay);
    }

    private static void PrintDownloadFailure(string cameraName, Uri uri, Exception exception)
    {
        Console.WriteLine($"Unable to process {uri.AbsolutePath} for {cameraName}: {exception.Message}");
    }

    private sealed record RemoteEntry(string Name, Uri Uri, bool IsDirectory, DateTime? FileTime);

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
        IReadOnlyList<CameraSnapshot> Cameras);

    private sealed class DownloadInterruptedException : IOException
    {
        public DownloadInterruptedException(Uri uri, Exception innerException)
            : base($"Transfer interrupted for {uri.AbsolutePath}.", innerException)
        {
        }
    }
}
