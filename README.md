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
                                                      Internet Archive · Torznab indexers
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
- **Torznab** (Jackett, Prowlarr, …) lets an admin add any indexer they run. Phantom ships with
  none configured. Only add indexers for content you have the right to download and stream;
  you are responsible for complying with copyright law where you live.

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
- Sources (Internet Archive queries, Torznab indexers), sync status, enable/disable
- Settings: server name, preferred quality, download/upload limits, max active downloads,
  stream cache lifetime, seeding, minimum seeders, auto-download
- Idle stream caches are evicted automatically

## Running the server

Requirements: .NET 9 SDK. Optional: `ffmpeg` on `PATH` (transcoding), a TMDB API key (metadata).

```bash
cd src/Phantom.Api
dotnet run                     # http://0.0.0.0:5180, Swagger UI at /swagger
```

Configuration lives in `appsettings.json` under `Phantom` (environment variables work too,
e.g. `Phantom__TmdbApiKey=...`):

| Key | Default | Meaning |
| --- | --- | --- |
| `DataDirectory` | `data` | Database, torrent cache, permanent media, JWT key |
| `TmdbApiKey` | empty | Enables rich metadata and collections |
| `AllowRegistration` | `false` | Open sign-up after the owner account exists |
| `FfmpegPath` | `ffmpeg` | Transcoder binary |
| `TorrentListenPort` | `55125` | BitTorrent/DHT port (forward it on your router for better speeds) |
| `SeedDefaultSources` | `true` | Add the Internet Archive source on first run |

The first account created becomes the administrator.

## Running the client

Requirements: .NET 9 SDK with the MAUI workload (`dotnet workload install maui`).

```bash
cd src/Phantom.Client
dotnet build -t:Run -f net9.0-android        # Android device or emulator
dotnet build -t:Run -f net9.0-ios            # macOS only
dotnet build -t:Run -f net9.0-maccatalyst    # macOS only
dotnet build -t:Run -f net9.0-windows10.0.19041.0  # Windows only
```

Launch the app, enter the server address (e.g. `http://192.168.1.10:5180`), and sign in.

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
  schema on an existing install.
- Device downloads run while the app is alive; there is no Android foreground download service yet.
- Seeking a transcoded stream restarts ffmpeg at the new position (direct play seeks instantly).
- MonoTorrent streams one file per torrent at a time; several viewers of the same torrent share it.
