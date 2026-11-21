# Docker Setup

The container reads camera addresses and names from numbered environment-variable pairs. Pair numbers must be consecutive, starting at 1.

## Docker Desktop

Select the latest `dashcamsync-dashcam-sync` image and open **Optional settings**.

Under **Environment variables**, replace the camera placeholders with valid values:

| Variable | Example value |
| --- | --- |
| `DASHCAM_CAMERA_1_IP` | `192.168.1.58` |
| `DASHCAM_CAMERA_1_NAME` | `Impreza Dashcam` |
| `DASHCAM_CAMERA_2_IP` | `192.168.1.57` |
| `DASHCAM_CAMERA_2_NAME` | `Forrester Dashcam` |

To add another camera, add `DASHCAM_CAMERA_3_IP` and `DASHCAM_CAMERA_3_NAME`, then continue with the next number for additional cameras. The program exits with an error if a configured pair is incomplete or still contains a placeholder.

For persistent downloads, add a volume mapping with the NAS folder on the left and `/volume1/Data/DashCam` on the right. Set `DASHCAM_DOWNLOAD_ROOT` to `/volume1/Data/DashCam`:

```text
/volume1/Data/DashCam:/volume1/Data/DashCam
```

The Compose project uses host networking. The dashboard listens directly on NAS port `8081`; no port mapping is used. Open `http://<NAS-IP>:8081` in a browser.

Each camera shows the number and total size of files currently in its download folder. These values are recalculated whenever the dashboard requests status, so manual deletions and additions are reflected on the next refresh. Incomplete `.download` files and application metadata are excluded.

Each camera's files are downloaded oldest first, based on the camera's file timestamps.

Select **Rescan camera** to interrupt the current transfer and rebuild that camera's download queue from its current file listing, local files, and download manifest. Partial downloads resume if still needed. Files recorded in `.downloaded-files.json` are not downloaded again even if removed locally, preserving storage-limit cleanup behavior.

Use the **Maximum size (GB)** control on a camera card to set its storage limit. One GB is 1,073,741,824 bytes; the default `0` means unlimited. Existing limits saved in MB are automatically migrated to equivalent GB limits. When a nonzero limit is exceeded, the oldest downloaded files are deleted until usage is within the limit. Automatic cleanup never deletes files inside folders named `RO`, though they count toward the camera's usage. Limits are saved in `.camera-size-limits.json` in the downloads root and persist across container restarts; keep this file with the download volume.

Select **Browse files** on a camera card to browse its `/DCIM/` folders. Each file shows whether a completed copy is on disk, was previously downloaded but is no longer on disk, or has not been downloaded. Status is checked when the folder is opened or reloaded using the expected local file path and the download manifest; manifest records expire after one year. Files without a usable timestamp may have an unavailable status. Use the checkboxes and **Delete selected** to delete multiple files from the current folder, or use **Delete** for one file. Selections are cleared when the folder is reloaded or changed. Both actions ask for confirmation, pause that camera's current download, request the camera's `cmd=4003` operation, and check the directory again. Afterward, the camera is rescanned and any interrupted download resumes from its partial file if the original is still available. If the interrupted file itself was deleted, its incomplete `.download` file remains on disk. The browser reports each batch failure and the number of verified deletions. This permanently deletes the selected originals from the camera, including files in `RO` folders. Downloaded copies, if any, are not deleted.

If the page is unreachable, recreate the Container Manager project after applying the updated Compose file and allow TCP port `8081` through the DSM firewall for your LAN. If launching the image directly instead of using the project, select host networking in Container Manager.

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
      DASHCAM_DASHBOARD_URL: http://+:8081
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
DOWNLOADS_HOST_PATH=/volume1/Data/DashCam
```

Recreate the container after changing the configuration:

```sh
docker compose up --build -d
```