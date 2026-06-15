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

### Paths must match qBittorrent

Convy uses the save path that qBittorrent reports (`SavePath`) **verbatim** to locate each
file. For linking to work, the downloaded data must be visible inside the Convy container at
the **exact same absolute path** qBittorrent uses to write it.

In practice this means mounting the same volume, at the same mount point, in both
containers. For example, if qBittorrent saves to `/downloads`, then Convy must also see that
data at `/downloads`:

```yaml
# qBittorrent
volumes:
  - /mnt/data/torrents:/downloads
# Convy
volumes:
  - /mnt/data/torrents:/downloads      # identical target path
  - /mnt/data/media:/media             # destinations from ConvyMappings.ini, same filesystem
```

If the paths differ, Convy will look for the source file at a path that doesn't exist in its
own filesystem and the link will be skipped (and retried) indefinitely.

## Rule syntax

`ConvyMappings.ini` holds one rule per line:

```
<condition>  =>  <output path>
```

Lines beginning with `#` or `;` are comments. Blank lines are ignored.

Example:

```
Category == Movies && Size > 1073741824            => /media/movies
Tags.Contains(anime) || Category == Anime          => /media/anime
State == StalledUpload && Ratio >= 2.0             => /media/seeding/done
Name == "My Favourite Show" && !Tags.Contains(skip) => /media/shows/favourite
SeedingTime > 86400                                => /media/archive
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
