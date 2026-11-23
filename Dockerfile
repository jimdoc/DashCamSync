FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY DashCamSync.csproj ./
RUN case "$(uname -m)" in \
    x86_64) rid=linux-x64 ;; \
    aarch64|arm64) rid=linux-arm64 ;; \
    *) echo "Unsupported build architecture: $(uname -m)" >&2; exit 1 ;; \
    esac && dotnet restore DashCamSync.csproj --runtime "$rid"

COPY . ./
RUN case "$(uname -m)" in \
    x86_64) rid=linux-x64 ;; \
    aarch64|arm64) rid=linux-arm64 ;; \
    *) echo "Unsupported build architecture: $(uname -m)" >&2; exit 1 ;; \
    esac && dotnet publish DashCamSync.csproj \
        --configuration Release \
        --runtime "$rid" \
        --self-contained true \
        --no-restore \
        --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:8.0

WORKDIR /app
COPY --from=build /app/publish ./

ENV DASHCAM_PORT=8081 \
    DASHCAM_DOWNLOAD_ROOT=/volume1/Data/DashCam \
    DASHCAM_REQUIRE_CAMERA_SETTINGS=true \
    DASHCAM_CAMERA_1_IP=ENTER_CAMERA_1_IP \
    DASHCAM_CAMERA_1_NAME=ENTER_CAMERA_1_NAME \
    DASHCAM_CAMERA_2_IP=ENTER_CAMERA_2_IP \
    DASHCAM_CAMERA_2_NAME=ENTER_CAMERA_2_NAME
VOLUME ["/volume1/Data/DashCam"]
EXPOSE 8081

ENTRYPOINT ["./DashCamSync"]