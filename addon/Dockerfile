# The add-on image is the Home Assistant base image plus the already-built
# application. Because the application is published self-contained, the base
# image needs no .NET runtime installed.

ARG BUILD_FROM=ghcr.io/home-assistant/amd64-base-debian:bookworm
ARG APP_IMAGE=ghcr.io/borexola/novee2mqtt:latest

FROM ${APP_IMAGE} AS app

FROM ${BUILD_FROM}

COPY --from=app /app /app

COPY run.sh /run.sh
RUN chmod +x /run.sh

# Add-ons get /data mounted as their persistent storage.
ENV GOVEE_CACHE_DIR=/data \
    DOTNET_EnableDiagnostics=0

CMD ["/run.sh"]
