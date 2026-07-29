# Builds a self-contained binary so the same artifact works both as a standalone
# container and inside the Home Assistant add-on base images, which have no .NET
# runtime of their own.

ARG DOTNET_VERSION=10.0

####################################################################################################
## Builder
####################################################################################################
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build

# The RID rewrite below needs bash; Debian's default sh is dash.
SHELL ["/bin/bash", "-c"]

ARG TARGETARCH
ARG VERSION=0.0.0

WORKDIR /src

# Restore separately so dependency changes, not source edits, invalidate the layer.
COPY Directory.Build.props ./
COPY src/Novee2Mqtt/Novee2Mqtt.csproj src/Novee2Mqtt/
RUN RID="linux-${TARGETARCH/amd64/x64}" && \
    dotnet restore src/Novee2Mqtt/Novee2Mqtt.csproj -r "$RID"

COPY src/ src/
RUN RID="linux-${TARGETARCH/amd64/x64}" && \
    dotnet publish src/Novee2Mqtt/Novee2Mqtt.csproj \
        --configuration Release \
        --runtime "$RID" \
        --self-contained true \
        --no-restore \
        -p:Version="${VERSION}" \
        -p:PublishSingleFile=false \
        -p:DebugType=none \
        --output /app

# An empty directory to copy into the runtime stage, which has no shell to mkdir with.
RUN mkdir -p /empty-data

####################################################################################################
## Runtime
####################################################################################################
# Chiseled: no shell and no package manager, just the native dependencies a
# self-contained .NET app needs. "extra" adds ICU and tzdata so log timestamps
# can honour $TZ.
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled-extra AS runtime

LABEL org.opencontainers.image.title="Novee2Mqtt" \
      org.opencontainers.image.description="Bridge between Govee devices and Home Assistant via MQTT" \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app

# Left owned by root and only read by the app; chown-ing it here would duplicate
# the entire ~110MB layer.
COPY --from=build /app /app

# The cache lives here; it survives restarts and keeps us inside Govee's rate limits.
COPY --from=build --chown=app:app /empty-data /data

USER app

# The base image defaults ASPNETCORE_HTTP_PORTS to 8080; clear both so Kestrel
# uses only the endpoint configured from --http-port.
ENV GOVEE_CACHE_DIR=/data \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_URLS= \
    ASPNETCORE_HTTP_PORTS=

VOLUME /data

# The chiseled image has no shell and no curl, so the app probes itself.
# start-period covers the ~15s LAN discovery wait during startup.
HEALTHCHECK --interval=60s --timeout=10s --start-period=60s --retries=3 \
    CMD ["/app/novee2mqtt", "health"]

# Web UI and REST API. LAN discovery additionally needs UDP 4002 on the host,
# which is why host networking is required.
EXPOSE 8056

ENTRYPOINT ["/app/novee2mqtt"]
CMD ["serve"]
