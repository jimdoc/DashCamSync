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