# DashCamSync
I created this project to automatically copy videos from my 2 Viofo dashcams to my NAS.

**NEVER** expose this web interface to the internet. This is designed to be accessed via an unencrypted local connection only. User authentication is not part of this project at this time. Authentication may be a future addition. Encryption could be done via reverse proxy but for now is out of scope for this project.

To make this work, the dash cams must be set to "station mode". Please contact Viofo customer support for special firmware for your dashcam that allows it to remain in station mode.

While parked at home, the camera must remain powered on for videos to be copied. To do this you must make sure that parking mode stays active long enough for files to be copied. I recommend a hard-wire kit that will power off before draining your battery.

This project is installed as a docker style package on my Synology NAS using the Synology Container Manager.

## Docker Setup

The container reads camera addresses and names from numbered environment-variable pairs. Pair numbers must be consecutive, starting at 1.

## Synology Container Manager Project

1. Install **Container Manager** from DSM's Package Center. Copy or clone the complete repository into a folder on the NAS, for example `/volume1/docker/DashCamSync`. The folder must contain `compose.yaml`, `Dockerfile`, `DashCamSync.csproj`, the source files, and `wwwroot`; the Compose file alone is not enough to build the image.
2. Create the download folder on the NAS, for example `/volume1/Data/DashCam`, and ensure the container can write to it. Keep the download folder separate from the source folder.
3. Open **Container Manager > Project** and select **Create**. Choose a project name and select the repository folder as the project path. The supplied `build: context: .` uses this folder as its build context.
4. Choose the option to create `docker-compose.yml` and paste in the contents of the repository's `compose.yaml`. In the configuration editor, replace the example camera values with your cameras' IP addresses and names. For example, change the corresponding entries under `environment` to:

   ```yaml
      DASHCAM_PORT: "8081"
      DASHCAM_CAMERA_1_IP: "192.168.1.58"
      DASHCAM_CAMERA_1_NAME: "Impreza Dashcam"
      DASHCAM_CAMERA_2_IP: "192.168.1.57"
      DASHCAM_CAMERA_2_NAME: "Forrester Dashcam"
   ```

   Keep `DASHCAM_REQUIRE_CAMERA_SETTINGS`, `DASHCAM_DOWNLOAD_ROOT`, and `network_mode: host` as supplied. To use a different download folder, change the host side of the `volumes` mapping, leaving the container side as `/volume1/Data/DashCam`. Remove both entries for camera 2 if you have only one camera, or add consecutive numbered pairs for additional cameras.
5. Continue through the wizard without configuring a Web Station web portal, review the settings, and create the project. Container Manager builds the image from the local source and starts the service. The first build requires internet access to obtain the .NET base images and restore dependencies.
6. Check the project and container logs for build or startup errors. Open `http://<NAS-IP>:8081` on your local network, substituting your chosen `DASHCAM_PORT` if different. Allow that TCP port through the DSM firewall for your LAN if necessary.

After updating the source or project configuration, rebuild and recreate the project using Container Manager so the changes take effect. Preserve the download folder when removing or recreating a project. Menu labels may vary between Container Manager versions.

## Docker Desktop

Select the latest `dashcamsync-dashcam-sync` image and open **Optional settings**.

Under **Environment variables**, replace the camera placeholders with valid values:

| Variable | Example value |
| --- | --- |
| `DASHCAM_PORT` | `8081` |
| `DASHCAM_CAMERA_1_IP` | `192.168.1.58` |
| `DASHCAM_CAMERA_1_NAME` | `Impreza Dashcam` |
| `DASHCAM_CAMERA_2_IP` | `192.168.1.57` |
| `DASHCAM_CAMERA_2_NAME` | `Forrester Dashcam` |

Set `DASHCAM_PORT` to the available port you want the dashboard to use; it defaults to `8081`.

To add another camera, add `DASHCAM_CAMERA_3_IP` and `DASHCAM_CAMERA_3_NAME`, then continue with the next number for additional cameras. The program exits with an error if a configured pair is incomplete or still contains a placeholder.

IP address should be statically assigned so they do not change. A local DNS name may also work, but I have not tested this.

For persistent downloads, add a volume mapping with the NAS folder on the left and `/volume1/Data/DashCam` on the right. Set `DASHCAM_DOWNLOAD_ROOT` to `/volume1/Data/DashCam`:

```text
/volume1/Data/DashCam:/volume1/Data/DashCam
```

The Compose project uses host networking. The dashboard listens directly on the port set by `DASHCAM_PORT` (default `8081`); no port mapping is used. Open `http://<NAS-IP>:<DASHCAM_PORT>` in a browser.

Each camera shows the number and total size of files currently in its download folder. These values are recalculated whenever the dashboard requests status, so manual deletions and additions are reflected on the next refresh. Incomplete `.download` files and application metadata are excluded.

Each camera's files are downloaded oldest first, based on the camera's file timestamps.

After startup, the app rescans each camera whenever a scan downloads files, repeating until a scan finds nothing new. Once caught up, it scans again hourly. Reconnection and manual rescan requests also trigger a scan.

Select **Rescan camera** to interrupt the current transfer and rebuild that camera's download queue from its current file listing, local files, and download manifest. Partial downloads resume if still needed. Files recorded in `.downloaded-files.json` are not downloaded again even if removed locally, preserving storage-limit cleanup behavior.

Use the **Maximum size (GB)** control on a camera card to set its storage limit. One GB is 1,073,741,824 bytes; the default `0` means unlimited. Existing limits saved in MB are automatically migrated to equivalent GB limits. When a nonzero limit is exceeded, the oldest downloaded files are deleted until usage is within the limit. Automatic cleanup never deletes files inside folders named `RO`, though they count toward the camera's usage. Limits are saved in `.camera-size-limits.json` in the downloads root and persist across container restarts; keep this file with the download volume.

Select **Browse files** on a camera card to browse its `/DCIM/` folders. Each file shows whether a completed copy is on disk, was previously downloaded but is no longer on disk, or has not been downloaded. Status is checked when the folder is opened or reloaded using the expected local file path and the download manifest; manifest records expire after one year. Files without a usable timestamp may have an unavailable status. Use the checkboxes and **Delete selected** to delete multiple files from the current folder, or use **Delete** for one file. Selections are cleared when the folder is reloaded or changed. Both actions ask for confirmation, pause that camera's current download, request the camera's `cmd=4003` operation, and check the directory again. Afterward, the camera is rescanned and any interrupted download resumes from its partial file if the original is still available. If the interrupted file itself was deleted, its incomplete `.download` file remains on disk. The browser reports each batch failure and the number of verified deletions. This permanently deletes the selected originals from the camera, including files in `RO` folders. Downloaded copies, if any, are not deleted.

If the page is unreachable, recreate the Container Manager project after applying the updated Compose file and allow the TCP port set by `DASHCAM_PORT` through the DSM firewall for your LAN. If launching the image directly instead of using the project, select host networking in Container Manager.

## Docker Compose

The supplied `compose.yaml` has two camera entries. Edit their values or add the next numbered pair under `environment`:

```yaml
      DASHCAM_CAMERA_3_IP: ${DASHCAM_CAMERA_3_IP:-}
      DASHCAM_CAMERA_3_NAME: ${DASHCAM_CAMERA_3_NAME:-}
```

The `volumes` entry maps a host folder to the container download directory:

```yaml
    network_mode: host
    environment:
      DASHCAM_PORT: ${DASHCAM_PORT:-8081}
      DASHCAM_DOWNLOAD_ROOT: /volume1/Data/DashCam
    volumes:
      - "${DOWNLOADS_HOST_PATH:-/volume1/Data/DashCam}:/volume1/Data/DashCam"
```

Change `DOWNLOADS_HOST_PATH` to choose a different NAS folder. Keep the container side of the volume (`/volume1/Data/DashCam`) the same as `DASHCAM_DOWNLOAD_ROOT`.

You can set camera values and the host folder in a `.env` file beside `compose.yaml`:

```dotenv
DASHCAM_CAMERA_1_IP=192.168.1.58
DASHCAM_CAMERA_1_NAME=Impreza Dashcam
DASHCAM_CAMERA_2_IP=192.168.1.57
DASHCAM_CAMERA_2_NAME=Forrester Dashcam
DASHCAM_PORT=8081
DOWNLOADS_HOST_PATH=/volume1/Data/DashCam
```

Recreate the container after changing the configuration:

```sh
docker compose up --build -d
```