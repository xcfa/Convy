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
3. For each reported torrent, Convy evaluates the rules in `rules.yaml` against the
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
   save path and every destination in `rules.yaml` must live on the **same mount**.

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

Rules live in a YAML file (`rules.yaml`) as an ordered list; the first matching rule wins.
The file is reloaded automatically when it changes. A torrent that matches no rule is
skipped and not re-evaluated until the rules change.

```yaml
rules:
  - condition: "Category == Movies && Size > 1073741824"
    path: /data/media/movies
  - condition: "Tags.Contains(anime) || Category == Anime"
    path: /data/media/anime
  - condition: "State == StalledUpload && Ratio >= 2.0"
    path: /data/media/seeding/done
  - condition: "Name == \"My Favourite Show\" && !Tags.Contains(skip)"
    path: /data/media/shows/favourite
  - condition: "SeedingTime > 86400"
    path: /data/media/archive
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

An unknown property, an illegal operator for a type, or a malformed rule fails fast with a
parse error that names the offending rule. A parse failure keeps the previously loaded
rules in effect rather than taking the service down.

## Configuration

Convy reads settings from several sources, applied in order (later wins):

1. `appsettings.json` — built-in defaults (logging, connection string)
2. `config/appsettings.json` — optional override mounted into the container
3. `config/configuration.yml` — **user configuration file** (webhooks, etc.)
4. Environment variables (double underscore = nesting)
5. Docker secrets (`/run/secrets`)
6. Command-line arguments

For day-to-day use, put your settings in `config/configuration.yml` and connection /
qBittorrent credentials in environment variables or Docker secrets.

### qBittorrent connection

| Variable | Description |
| --- | --- |
| `QBITTORRENT__URL` | Base URL of the qBittorrent Web UI |
| `QBITTORRENT__USERNAME` | Web UI username |
| `QBITTORRENT__PASSWORD` | Web UI password |
| `QBITTORRENT__SYNCINTERVAL` | Poll interval as `h:m:s`; `0` disables syncing |

### Other settings

- `Convy:RulesPath` — path to the YAML rules file (default `config/rules.yaml`)
- `ConnectionStrings:SQLite` — EF Core/SQLite connection string for the local database
  that stores linked files and tracker state

### Webhooks

A webhook fires once at the end of each sync cycle if at least one torrent was linked
or an error occurred. The request is a single POST with a JSON body containing all
results at once. Configured in `config/configuration.yml`:

```yaml
webhooks:
  - name: Send to telegram
    url: https://tg-proxy.example.com/34234324
    params:
      - place: Body
        name: category
        value: category
      - place: Body
        name: torrent
        value: name

  - name: Simple hook
    url: https://example.com/hook
```

The POST body is always a JSON object with two arrays:

```json
{
  "linked": [
    {"category": "Movies", "torrent": "My Film"},
    {"category": "Anime", "torrent": "Show S02"}
  ],
  "errors": [
    {"hash": "af83…", "error": "Link failed: EXDEV"}
  ]
}
```

Each param maps a torrent property to a field in each `linked` item:

| Field | Description |
| --- | --- |
| `place` | `Query` (URL query string) or `Body` (JSON body). Default: `Query` |
| `name` | Parameter name in the request |
| `value` | Torrent property to read (case-insensitive) |

Available property values: `hash`, `name`, `category`, `savePath`, `targetPath`, `size`,
`state`, `tags`.

When `params` is omitted, every property is included in each `linked` item.

**Environment variable shorthand.** When a full YAML config isn't needed, a webhook URL
can be set via environment variables:

```
Webhooks__0__Url=https://example.com/hook
Webhooks__0__Name=My hook
```

## Running

1. Copy `.env.example` to `.env` and fill in your qBittorrent details. `.env` is
   git-ignored and never committed.
2. Provide a `config/rules.yaml` with your rules.
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
    environment:
      CONVY_CONNECTIONSTRING: "Data Source=/var/lib/convy/convy.db"
      QBITTORRENT__URL: ""
      QBITTORRENT__USERNAME: ""
      QBITTORRENT__PASSWORD: ""
      QBITTORRENT__SYNCINTERVAL: "01:00:00"
    volumes:
      # One filesystem holding downloads AND media as subfolders, mounted at the SAME
      # absolute path qBittorrent uses for its save path.
      - /srv/media-stack:/data
      # User config: routing rules (rules.yaml) and webhooks (configuration.yml)
      - ./config:/app/config:ro
      # Persist the database. Optional — without it the db is ephemeral.
      - convy-db:/var/lib/convy

volumes:
  convy-db:
```

Notes:

- **qBittorrent must mount the same storage at the same path.** If qBittorrent runs in
  another container/host, give it the same `/srv/media-stack:/data` mount and let it save
  under `/data/downloads/...`. Convy then links into `/data/media/...` on the same filesystem.
- The destinations in `rules.yaml` must live under that same mount (e.g. `/data/media/...`).
- The image runs as **root** by default and needs no mounts to start. To run as a non-root
  user, set `user:` in compose — then it's on you to make `/var/lib/convy` (the db) and the
  `/data` tree writable by that uid (e.g. `chown` them, or use volumes owned by it).

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
| `Convy.Services` | qBittorrent communication, file linking, state tracker, webhook notifier |
| `Convy.PathExpressions` | the rule language: ANTLR grammar, expression tree, mapping-file loader |
| `Convy.Data` | EF Core (SQLite) entities and migrations |
| `Convy.Infrastructure` | low-level helpers (the native hard-link wrapper) |
| `Tests/*` | unit tests for the rule language and the state tracker |
