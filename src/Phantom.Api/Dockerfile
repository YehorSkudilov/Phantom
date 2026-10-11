# Build from the repository root (the API references Phantom.Shared):
#   docker build -f src/Phantom.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

# ffmpeg for on-the-fly transcoding (Services/Transcoder.cs).
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

# The data volume (database, torrents, media) must be writable by the non-root app user; a new named volume takes
# this directory's ownership.
RUN mkdir -p /data && chown "$APP_UID" /data

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Phantom.Api/Phantom.Api.csproj", "src/Phantom.Api/"]
COPY ["src/Phantom.Shared/Phantom.Shared.csproj", "src/Phantom.Shared/"]
RUN dotnet restore "src/Phantom.Api/Phantom.Api.csproj"

COPY src/Phantom.Api/ src/Phantom.Api/
COPY src/Phantom.Shared/ src/Phantom.Shared/
RUN dotnet publish "src/Phantom.Api/Phantom.Api.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Phantom.Api.dll"]
