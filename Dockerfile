# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release

WORKDIR /src
COPY MqttVision.Server.csproj ./
RUN dotnet restore MqttVision.Server.csproj

COPY . ./
RUN dotnet publish MqttVision.Server.csproj \
    --configuration ${BUILD_CONFIGURATION} \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl fontconfig libgdiplus \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 appgroup \
    && useradd --system --uid 10001 --gid appgroup --home-dir /app --shell /usr/sbin/nologin appuser

WORKDIR /app
COPY --from=build /app/publish ./
# The ONNX model is a deployment asset and is not emitted by dotnet publish by default.
COPY Models/yolo-best.onnx ./Models/yolo-best.onnx

RUN mkdir --parents /app/runtime /app/Configuration /app/config \
    && chown --recursive appuser:appgroup /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 5080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:5080/api/health || exit 1

USER appuser
ENTRYPOINT ["dotnet", "MqttVision.Server.dll"]
