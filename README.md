<p align="center">
  <img src="docs/brand/app-icon.svg" alt="Phantom logo" width="120">
</p>

<h1 align="center">Phantom</h1>

<p align="center"><b>Your movies, streamed from the swarm.</b></p>

Phantom is a self-hosted, Plex-style movie server and app. The **server** (ASP.NET Core Web API)
builds its library automatically from torrent sources and streams any title while it downloads.
The **client** (.NET MAUI) gives you a Plex-like experience on Android, iOS, macOS and Windows.

```
┌────────────────────┐   REST + HTTP range streams   ┌──────────────────────────────────┐
│  Phantom.Client    │ ────────────────────────────▶ │  Phantom.Api                     │
│  .NET MAUI app     │                               │  Controllers · EF Core (SQLite)  │
│  offline downloads │ ◀──────────────────────────── │  MonoTorrent engine · ffmpeg      │
└────────────────────┘                               │  Source sync · TMDB metadata     │
                                                     └──────────────┬───────────────────┘
                                                                    │ BitTorrent / web seeds
                                                      Internet Archive · YTS · TPB · Torznab
```

## Projects

| Path | What it is |
| --- | --- |
| `src/Phantom.Api` | ASP.NET Core Web API with controllers: library, streaming, server downloads, sources, users |
| `src/Phantom.Client` | .NET MAUI client (MVVM with CommunityToolkit) |
| `src/Phantom.Shared` | DTOs shared by both |
| `tests/Phantom.Api.Tests` | xUnit unit and integration tests |
| `docs/brand` | Logo, icon, palette |

## Content sources (read this)

Phantom pulls movies automatically, but **you decide where from**:

- **Internet Archive – Feature Films** is enabled out of the box. These are public-domain films,
  and every archive.org torrent carries HTTP web seeds, so playback starts even with no peers.
- **YTS** and **The Pirate Bay** are built in. Add them from *Admin → + Add source* in one tap;
  no Jackett or Prowlarr needed. YTS brings posters, genres and IMDb ids with every movie; the
  Pirate Bay source pulls the top 100 of Movies, HD Movies and UHD Movies (or a search query).
- **Torznab** (Jackett, Prowlarr, …) is still there for any other indexer you run.

Only add sources for content you have the right to download and stream; you are responsible
for complying with copyright law where you live.

## Features

**Library and discovery**
- Automatic, scheduled imports from every enabled source, plus "Sync now"
- Release-name parsing (title, year, quality, codec) and de-duplication across sources
- Optional TMDB metadata: posters, backdrops, cast and crew, trailers, US certification, studio,
  and automatic collections (`Phantom:TmdbApiKey`)
- Home hubs: Continue Watching, Watchlist, Recently Added, Recommended For You, Recently
  Released, Top Rated, Available On Server, Rediscover, plus top genres
- Library grid or list view, sorting (added, title, year, rating, duration, last viewed,
  random), filters (genre, decade, quality, content rating, watched state), A–Z jump bar,
  infinite scroll
- Search across titles, people and genres; tap an actor or director to see their films
- Movie page: backdrop, tagline, overview, cast, director and writer, genres, versions (pick
  the torrent release), More Like This

**Playback**
- Direct play over HTTP range requests, straight from a torrent that is still downloading
  (sequential piece picking around the playhead; several concurrent readers are supported)
- Optional ffmpeg transcoding to H.264/AAC fragmented MP4 with quality presets
  (1080p/8 Mbps down to 360p/0.7 Mbps)
- Resume prompt, "play from beginning", watched at 90%, view counts, star ratings
- Subtitles from the torrent (SRT/VTT) drawn by the player, with size control and a
  preferred language
- Skip back and forward (configurable), playback speed, up-next and auto-play for playlists
- Picture-in-picture capable Android activity; screen stays on while playing

**Lists and people**
- Watchlist, playlists (play, shuffle, reorder, remove), collections, watch history
- Plex Home-style managed profiles with PINs and parental content-rating limits; switching
  back to the owner requires the account password
- Now Playing dashboard for admins

**Downloads, two kinds**
- **Server downloads** (`/api/downloads`): the server fetches the whole torrent, moves it into
  `data/media`, optionally keeps seeding, and serves it from disk from then on. Concurrency
  and rate limits, pause/resume/cancel, auto-download of new imports
- **Device downloads** (client): copy a movie to the phone or tablet for offline viewing, with
  resumable HTTP range transfers, Wi-Fi-only mode, pause/resume, and offline resume points
  that sync back to the server when you reconnect

**Server admin**
- Sources (YTS, The Pirate Bay, Internet Archive queries, Torznab indexers), sync status, enable/disable
- Settings: server name, preferred quality, download/upload limits, max active downloads,
  stream cache lifetime, seeding, minimum seeders, auto-download
- Idle stream caches are evicted automatically

## Running the server

Requirements: .NET 10 SDK. Optional: `ffmpeg` on `PATH` (transcoding), a TMDB API key (metadata).

```bash
cd src/Phantom.Api
cp .env.example .env           # then set at least SwaggerAuth__Password
dotnet run                     # http://0.0.0.0:5180, Swagger UI at /swagger (basic auth)
```

Every setting lives in `src/Phantom.Api/.env` (there are no appsettings files). The API loads it itself under
`dotnet run`, and `docker-compose.yml` passes it to the container. See `.env.example` for all keys; the main ones:

| Key | Default | Meaning |
| --- | --- | --- |
| `Phantom__DataDirectory` | `data` | Database, torrent cache, permanent media, JWT key (`/data` volume in Docker) |
| `Phantom__JwtSigningKey` | empty | Token signing key; empty generates one in the data directory |
| `Phantom__TmdbApiKey` | empty | Enables rich metadata and collections |
| `Phantom__AllowRegistration` | `false` | Open sign-up after the owner account exists |
| `Phantom__TorrentListenPort` | `55125` | BitTorrent/DHT port (forward it on your router for better speeds) |
| `SwaggerAuth__Username` / `__Password` | required | Basic auth in front of the Swagger UI |
| `ReverseProxy__KnownNetworks` | empty | Proxies whose `X-Forwarded-*` headers are trusted |
| `API_PORT`, `TORRENT_PORT` | `8080`, `55125` | Host ports published by `docker-compose.yml` |

The first account created becomes the administrator. `GET /health` returns 200 once the API is up.

### Docker

```bash
cd src/Phantom.Api
docker compose up -d --build   # builds from the repo root, data in the phantom_data volume
```

The image includes ffmpeg and runs as a non-root user. Put a TLS-terminating reverse proxy in front of `API_PORT`.

## Running the client

Requirements: .NET 10 SDK with the MAUI workload (`dotnet workload install maui`).

```bash
cd src/Phantom.Client
cp .env.example .env           # required: set API_BASE_URL to your server
dotnet build -t:Run -f net10.0-android        # Android device or emulator
dotnet build -t:Run -f net10.0-ios            # macOS only
dotnet build -t:Run -f net10.0-maccatalyst    # macOS only
dotnet build -t:Run -f net10.0-windows10.0.19041.0  # Windows only
```

Launch the app, enter the server address (e.g. `http://192.168.1.10:5180`), and sign in.

## Deployment

Deployment works the same way as GymBook's: everything is started by hand from the **Actions → Publish** workflow
(`.github/workflows/Publish.yml`), which calls two reusable workflows.

**API (`PublishApi.yml`)**, from `main` only: SSH to the server, `git reset --hard origin/main` in `/opt/Phantom`,
write `src/Phantom.Api/.env` from repository variables and secrets, `docker compose build --pull && up -d`, wait
for `/health`, prune old images. One-time server setup: install Docker, `git clone` this repository to
`/opt/Phantom`, and open `TORRENT_PORT` (TCP and UDP) in the firewall.

**App (`PublishApp.yml`)**: bumps `ApplicationDisplayVersion`/`ApplicationVersion` in `Phantom.Client.csproj` and
commits it, builds each selected platform with `API_BASE_URL` baked in, uploads it, then creates a GitHub Release
`vX.Y.Z` with the Android `.aab` and `.apk`.

| Platform | Runner | Output | Uploaded to |
| --- | --- | --- | --- |
| Android | windows-latest | signed `.aab` + `.apk` | Google Play (chosen track) |
| Windows | windows-latest | `.msix` | Microsoft Store (new submission) |
| iOS | xcode-27 | `.ipa` | TestFlight |
| macOS | xcode-27 | signed `.pkg` | App Store Connect |

Repository **variables**: `API_BASE_URL`, `API_PORT`, `TORRENT_PORT`, `SWAGGER_AUTH_USERNAME`,
`ALLOW_REGISTRATION`, `TOKEN_LIFETIME_DAYS`, `IOS_XCODE_VERSION`, `MSSTORE_PACKAGE_NAME`, `MSSTORE_PUBLISHER`,
`MSSTORE_PUBLISHER_DISPLAY_NAME`, `MSSTORE_PRODUCT_ID`.

Repository **secrets**:
- Server: `SERVER_HOST`, `SERVER_USER`, `SERVER_SSH_KEY`, `JWT_SIGNING_KEY`, `SWAGGER_AUTH_PASSWORD`, `TMDB_API_KEY`
- Android: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASS`, `ANDROID_KEYALIAS_NAME`, `ANDROID_KEYALIAS_PASS`,
  `SERVICE_ACCOUNT_JSON`
- Windows: `AZURE_AD_TENANT_ID`, `AZURE_AD_CLIENT_ID`, `AZURE_AD_CLIENT_SECRET`, `MSSTORE_SELLER_ID`
- Apple: `IOS_P12_BASE64`, `IOS_P12_PASSWORD`, `IOS_PROFILE_BASE64`, `APPSTORE_KEY_ID`, `APPSTORE_ISSUER_ID`,
  `APPSTORE_PRIVATE_KEY`, and for macOS `MACOS_INSTALLER_P12_BASE64`, `MACOS_INSTALLER_P12_PASSWORD`,
  `MACOS_PROFILE_BASE64`

## Tests

```bash
dotnet test tests/Phantom.Api.Tests
```

## API overview

| Area | Endpoints |
| --- | --- |
| Auth & profiles | `POST /api/auth/register`, `POST /api/auth/login`, `GET /api/auth/me`, `GET/POST/DELETE /api/profiles`, `POST /api/profiles/switch`, `/api/users` (admin) |
| Library | `GET /api/library/movies`, `/filters`, `/hubs`, `/search` |
| Movies | `GET /api/movies/{id}`, `/related`, `POST /watched`, `/unwatched`, `PUT /rating`, `POST /refresh`, `DELETE` (admin) |
| Streaming | `GET /api/stream/{id}/info`, `/direct` (ranges), `/transcode`, `/subtitles/{n}` |
| Playback | `POST /api/playback/timeline`, `GET /sessions` (admin), `GET /history` |
| Lists | `/api/watchlist`, `/api/playlists`, `/api/collections` |
| Server downloads | `GET/POST /api/downloads`, `POST /{id}/pause`, `/resume`, `DELETE /{id}` |
| Sources (admin) | `GET/POST/PUT/DELETE /api/sources`, `POST /api/sources/{id}/sync` |
| Server | `GET /api/server/info`, `GET/PUT /api/server/settings` (admin) |

Media endpoints also accept the JWT as an `access_token` query parameter because video players
can't send headers. Put the server behind HTTPS if you expose it beyond your LAN.

## Known limitations

- The database is created with `EnsureCreated`; add EF Core migrations before changing the
  schema on an existing install (redeploys keep the `phantom_data` volume, so this matters).
- Device downloads run while the app is alive; there is no Android foreground download service yet.
- Seeking a transcoded stream restarts ffmpeg at the new position (direct play seeks instantly).
- MonoTorrent streams one file per torrent at a time; several viewers of the same torrent share it.
