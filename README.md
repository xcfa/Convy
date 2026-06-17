# Convy

Convy watches a qBittorrent instance and, whenever a torrent finishes downloading,
hard-links its files into an output directory chosen by a set of rules. The original
download stays in place (so seeding continues); the link just gives you a second,
organized view of the content with no extra disk usage.

Routing rules are written in a small expression language and evaluated top to bottom —
the first matching rule wins.

## How it works

1. A background worker polls qBittorrent's sync API on an interval.
2. A state tracker compares each torrent against the last state it acted on, and reports
   only the ones that have just become *downloaded* (or whose size changed). This state
   is persisted, so a restart does not reprocess everything — only what changed while the
   service was down.
3. For each reported torrent, Convy evaluates the rules in `ConvyMappings.ini` against the
   torrent's metadata. The first matching rule's path becomes the destination.
4. Every file that isn't already linked is hard-linked into the destination. Successfully
   linked files are recorded, so the work is idempotent and a transient failure (for
   example, an unmounted output directory) is retried on a later cycle instead of being
   lost.

Because hard links are used, the torrent's save path and the destination must live on the
**same filesystem**. Linking is done with the POSIX `link()` syscall, so Convy is meant to
run on Linux (the provided container image).

### Storage layout: one filesystem for downloads and destinations

Two hard requirements, both coming from how hard links work:

1. **Same path as qBittorrent.** Convy locates each file using the save path qBittorrent
   reports (`SavePath`) **verbatim**. The downloaded data must be visible inside the Convy
   container at the **exact same absolute path** qBittorrent writes it to. If it isn't,
   Convy finds nothing to link and keeps retrying.
2. **Same filesystem (mount) for source and destination.** A hard link cannot cross
   filesystems — the kernel returns `EXDEV` (errno 18) and the link fails. The qBittorrent
   save path and every destination in `ConvyMappings.ini` must live on the **same mount**.

The simplest way to satisfy both is a single data volume holding the downloads and the
media library side by side, mounted at the same path everywhere:

```
/data
├── downloads/        # qBittorrent's save path, e.g. /data/downloads/...
└── media/            # Convy's destinations,    e.g. /data/media/...
```

Do **not** mount downloads and media as two separate volumes (e.g. `/downloads` and
`/media`): even if they point at the same disk, Docker presents them as separate mounts and
`link()` fails with `EXDEV`. Use one mount with both as subdirectories.

To verify inside the container that two paths share a filesystem, compare their device ids —
the first number must match:

```bash
stat -c '%d %n' /data/downloads /data/media
```

## Rule syntax

`ConvyMappings.ini` holds one rule per line:

```
<condition>  =>  <output path>
```

Lines beginning with `#` or `;` are comments. Blank lines are ignored.

Example:

```
Category == Movies && Size > 1073741824            => /data/media/movies
Tags.Contains(anime) || Category == Anime          => /data/media/anime
State == StalledUpload && Ratio >= 2.0             => /data/media/seeding/done
Name == "My Favourite Show" && !Tags.Contains(skip) => /data/media/shows/favourite
SeedingTime > 86400                                => /data/media/archive
```

### Conditions

- Comparisons: `==`, `!=`, `>`, `>=`, `<`, `<=`
- Logic: `&&`, `||`, `!`, and parentheses for grouping
- Collection membership: `Tags.Contains(value)`
- Values: numbers, `true` / `false`, bare words (`Movies`) or quoted strings (`"My Show"`)
- String and tag matching is case-insensitive
- String and boolean properties support only `==` and `!=`; lists support only `.Contains(...)`

### Properties

The left-hand side of a comparison is any qBittorrent torrent property. Common ones:

| Property | Meaning |
| --- | --- |
| `Size`, `TotalSize`, `Downloaded`, `Uploaded` | byte counts |
| `Ratio`, `Progress`, `Availability` | floats |
| `Category`, `Name`, `Tracker`, `SavePath`, `ContentPath` | strings |
| `Tags` | tag list (use `Tags.Contains(...)`) |
| `State` | matched by name, e.g. `State == StalledUpload` |
| `AutoTmmEnabled`, `SequentialDownloadEnabled`, `SuperSeedingEnabled` | booleans |
| `SeedingTime`, `TimeActive`, `EstimatedTimeArrival` | durations, compared in **seconds** |
| `AddedOn`, `CompletionOn`, `LastActivity` | timestamps, compared as **unix seconds** |

An unknown property, an illegal operator for a type, or a malformed line fails fast with a
parse error that names the offending line.

## Configuration

Settings are supplied via environment variables (double underscore = nesting). With Docker
Compose they come from a local `.env` file (see `.env.example`):

| Variable | Description |
| --- | --- |
| `QBITTORRENT__URL` | Base URL of the qBittorrent Web UI |
| `QBITTORRENT__USERNAME` | Web UI username |
| `QBITTORRENT__PASSWORD` | Web UI password |
| `QBITTORRENT__SYNCINTERVAL` | Poll interval as `h:m:s`; `0` disables syncing |

Other settings:

- `Convy:MappingsFile` — path to the rules file (default `config/ConvyMappings.ini`)
- `ConnectionStrings:SQLite` — EF Core/SQLite connection string for the local database
  that stores linked files and tracker state

## Running

1. Copy `.env.example` to `.env` and fill in your qBittorrent details. `.env` is
   git-ignored and never committed.
2. Provide a `config/ConvyMappings.ini` with your rules.
3. Start it:

```bash
docker compose up -d --build
```

### Example docker-compose.yml

A minimal deployment using the published image. The single `/data` mount is what makes hard
links work (see [Storage layout](#storage-layout-one-filesystem-for-downloads-and-destinations)).

```yaml
services:
  convy:
    image: ghcr.io/xcfa/convy:latest
    container_name: convy
    restart: unless-stopped
    env_file: .env                       
    environment:
      CONVY_CONNECTIONSTRING: "Data Source=/appdata/convy.db"
      QBITTORRENT__URL: ""
      QBITTORRENT__USERNAME: ""
      QBITTORRENT__PASSWORD: ""
      QBITTORRENT__SYNCINTERVAL: "01:00:00"
    volumes:
      # One filesystem holding downloads AND media as subfolders, mounted at the SAME
      # absolute path qBittorrent uses for its save path.
      - /srv/media-stack:/data
      # Routing rules -> /app/config/ConvyMappings.ini
      - ./config:/app/config:ro
      # Persist the link/tracker database across restarts.
      - convy-db:/appdata

volumes:
  convy-db:
```

Notes:

- **qBittorrent must mount the same storage at the same path.** If qBittorrent runs in
  another container/host, give it the same `/srv/media-stack:/data` mount and let it save
  under `/data/downloads/...`. Convy then links into `/data/media/...` on the same filesystem.
- The destinations in `ConvyMappings.ini` must live under that same mount (e.g. `/data/media/...`).
- Convy's container runs as a fixed non-root user, so the `/data` tree must be readable
  (downloads) and writable (the destination subtree) by it.

## Building and testing

Requires the .NET 10 SDK.

```bash
dotnet build Convy.slnx
dotnet test Convy.slnx
```

The expression language is generated from an ANTLR grammar at build time; the build task
downloads a JRE automatically the first time, so no separate Java installation is needed.

## Project layout

| Project | Responsibility |
| --- | --- |
| `Convy` | ASP.NET host: the polling worker, dependency injection, HTTP endpoints |
| `Convy.Services` | qBittorrent communication, file linking, and the state tracker |
| `Convy.PathExpressions` | the rule language: ANTLR grammar, expression tree, mapping-file loader |
| `Convy.Data` | EF Core (SQLite) entities and migrations |
| `Convy.Infrastructure` | low-level helpers (the native hard-link wrapper) |
| `Tests/*` | unit tests for the rule language and the state tracker |
