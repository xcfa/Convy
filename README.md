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
  - name: movies                                  # optional; lets webhooks target this rule
    condition: "Category == Movies && Size > 1073741824"
    path: /data/media/movies
  - name: anime
    condition: "Tags.Contains(anime) || Category == Anime"
    path: /data/media/anime
  - condition: "State == StalledUpload && Ratio >= 2.0"
    path: /data/media/seeding/done
  - condition: "Name == \"My Favourite Show\" && !Tags.Contains(skip)"
    path: /data/media/shows/favourite
  - condition: "SeedingTime > 86400"
    path: /data/media/archive
  - name: soulseek-other                          # Soulseek downloads not placed above
    condition: "Provider == slskd"
    path: /data/media/soulseek
```

The optional `name` on a rule lets you scope webhooks to specific rules — see
[Webhooks](#webhooks).

### Conditions

- Comparisons: `==`, `!=`, `>`, `>=`, `<`, `<=`
- Logic: `&&`, `||`, `!`, and parentheses for grouping
- Collection membership: `Tags.Contains(value)`
- Values: numbers, `true` / `false`, bare words (`Movies`) or quoted strings (`"My Show"`)
- String and tag matching is case-insensitive
- String and boolean properties support only `==` and `!=`; lists support only `.Contains(...)`

### Properties

The left-hand side of a comparison is a property of the download. `Provider` is available
on every item; the rest come from the downloader (for qBittorrent, any torrent property).
Common ones:

| Property | Meaning |
| --- | --- |
| `Provider` | downloader that owns the item: `qbittorrent` or `slskd` |
| `Username` | Soulseek peer the files come from (slskd items only) |
| `Size`, `TotalSize`, `Downloaded`, `Uploaded` | byte counts |
| `Ratio`, `Progress`, `Availability` | floats |
| `Category`, `Name`, `Tracker`, `SavePath`, `ContentPath` | strings |
| `Tags` | tag list (use `Tags.Contains(...)`) |
| `State` | matched by name, e.g. `State == StalledUpload` |
| `AutoTmmEnabled`, `SequentialDownloadEnabled`, `SuperSeedingEnabled` | booleans |
| `SeedingTime`, `TimeActive`, `EstimatedTimeArrival` | durations, compared in **seconds** |
| `AddedOn`, `CompletionOn`, `LastActivity` | timestamps, compared as **unix seconds** |

**Missing properties.** Downloaders publish only the properties they know about: a Soulseek
download has no `Ratio` or `Tags`. If a rule references at least one property the item
doesn't have, the whole rule is skipped for that item — a single comparison is not treated as
false, otherwise `!Tags.Contains(skip)` would be true for every Soulseek download. A property
that exists but is unset (e.g. no category) still takes part and simply never matches.

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

Webhooks are configured here or in the [web UI](#web-ui), which also tests them; both sets
are used. Webhooks subscribe to events with `events` (default: only `linked`, so existing setups behave
as before):

| Event | When | Body |
| --- | --- | --- |
| `linked` | once per sync cycle, if something was placed or failed | `{ "linked": [...], "errors": [...] }` (below); items of agent jobs also carry `job_id` and `provider` |
| `job_status` | on every job status change, one POST each (one per job, however many releases it has) | see below |
| `source_error` | a source turns `auth_failed` or `error` | `{ "event", "source", "status", "message" }` |

`job_status` and `source_error` are delivered in order by a background queue and retried up to
5 times with a growing delay. `job_status` body (`files` lists at most 200 placed files;
`files_total` has the full count):

```json
{
  "event": "job_status",
  "job_id": "j_42",
  "status": "completed",
  "previous_status": "placing",
  "provider": "slskd",
  "category": "music",
  "rule": "music",
  "title": "…",
  "path": "/data/media/music/Evanescence/2003 - Fallen",
  "files": ["01 - Going Under.flac", "…"],
  "files_total": 12,
  "size_bytes": 432000000,
  "error": null
}
```

A job with several releases (see [Jobs and placement](#jobs-and-placement)) also carries
`"releases": [{ "title", "status", "provider", "rule", "path", "size_bytes", "error" }, …]`;
its `path` is the directory holding all releases, `files` are relative to it, and `rule` is set
only when every release has the same one.

The `names` filter applies to `job_status` through the job's rules (any of its releases'); `source_error` events go to
every subscribed webhook. With `params`, an event body contains only the selected fields (query
params are added to the URL). Post-processing such as tagging or a library rescan belongs in the
receiver (e.g. n8n) reacting to `job_status`. Webhook changes in `configuration.yml` apply
without a restart.

```yaml
webhooks:
  - name: Jobs to n8n
    url: http://n8n:5678/webhook/media
    events: [job_status, source_error]
```

The `linked` webhook fires once at the end of each sync cycle if at least one item was linked
or an error occurred. The request is a single POST with a JSON body containing all
results at once. Configured in `config/configuration.yml`:

```yaml
webhooks:
  - name: Send anime to telegram
    url: https://tg-proxy.example.com/34234324
    names:                       # only rules named "anime" reach this webhook
      - anime
    params:
      - place: Body
        name: category
        value: category
      - place: Body
        name: torrent
        value: name

  - name: Catch-all hook          # no `names` -> fires for every rule
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
`state`, `tags`; for items of agent jobs also `job_id` and `provider`.

When `params` is omitted, every property is included in each `linked` item.

**Scoping to rules.** The optional `names` list restricts a webhook to torrents routed by
rules with those names (`name` in `rules.yaml`). When `names` is omitted or empty, the
webhook fires for torrents matched by any rule. Torrents routed by an unnamed rule reach
only webhooks without a `names` filter. Errors aren't attributable to a rule, so they are
delivered to every webhook that fires.

**Environment variable shorthand.** When a full YAML config isn't needed, a webhook URL
can be set via environment variables:

```
Webhooks__0__Url=https://example.com/hook
Webhooks__0__Name=My hook
Webhooks__0__Names__0=anime
```

## MCP: search and download

Convy exposes an [MCP](https://modelcontextprotocol.io) endpoint at `/mcp` (streamable HTTP)
for an LLM agent such as opencode. The agent searches several sources, looks into a result's
files and starts the download; Convy adds it to the download client right away and later
places the files with the same rules as everything else. Choosing a release is the agent's
job — Convy does not parse release names.

Sources in this version: torrent trackers through **Prowlarr** (one source per indexer, id
`prowlarr:<indexer id>`) and **Soulseek** through slskd (id `soulseek`). Torrents are
downloaded by qBittorrent, Soulseek folders by slskd.

### Connecting

| Variable | Purpose |
| --- | --- |
| `MCP__APIKEY` | Key the agent must send as `X-Api-Key` or `Authorization: Bearer`. Without it `/mcp` answers `503`. |
| `PROWLARR__URL`, `PROWLARR__APIKEY` | Prowlarr connection. Without them no tracker source is offered. |
| `SLSKD__URL`, `SLSKD__APIKEY` | slskd connection. Without them there is no Soulseek source and no slskd downloader. |
| `SLSKD__DOWNLOADSPATH` | Optional: slskd's downloads directory as seen inside Convy, when it differs from slskd's own `directories.downloads`. |
| `TORRENTMETADATA__CACHEDIRECTORY`, `TORRENTMETADATA__DHTPORT` | Optional: DHT cache directory and UDP port (0 = any) used to read magnet metadata. |

`/mcp` is also subject to the IP allow-list. Example opencode configuration:

```json
{
  "mcp": {
    "convy": {
      "type": "remote",
      "url": "http://convy:8080/mcp",
      "headers": { "X-Api-Key": "{env:CONVY_MCP_KEY}" }
    }
  }
}
```

Categories, search and file-list settings live in `config/configuration.yml` (see the
commented example there). The `other` category is mandatory.

### Agent skill

The tools tell the agent *what* it can call; [`skills/convy/SKILL.md`](skills/convy/SKILL.md)
tells it *how* to work with Convy: the order of calls, when to look at the file list, how to
build `subpath` from `path_hint`, which releases to prefer, what job statuses and errors mean,
and that a download needs the user's confirmation. It follows the Agent Skills format, so
opencode, Claude Code and other clients that support skills load it by its description.

Copy the folder to one of the skill locations, e.g. for opencode globally:

```bash
mkdir -p ~/.config/opencode/skills
cp -r skills/convy ~/.config/opencode/skills/
```

opencode also reads `~/.claude/skills/` and `~/.agents/skills/` (and `.opencode/skills/`,
`.claude/skills/`, `.agents/skills/` in a project). The release preferences in the skill
(2160p HDR with a Russian audio track, BDRip over BDRemux, lossy music) are the defaults of
this setup; edit the "Release preferences" section to change them.

### Tools

| Tool | Parameters | Returns |
| --- | --- | --- |
| `get_categories` | — | categories with `description`, `path_hint` and sources in priority order |
| `get_sources` | — | sources with protocol and status (`ok` / `error` / `disabled`) |
| `search` | `category`, `queries[]`, `sources[]?` | `search_id`, results of the first batch, a status per source, `has_more` |
| `search_next` | `search_id` | results of the next batch of sources, `has_more` |
| `list_files` | `result_id`, `path?`, `glob?`, `offset?` | top-level tree with per-directory summary, one expanded directory, or glob matches; or `status: timeout` |
| `download` | `category`, and `result_id` + `subpath?`, `include[]?`, `exclude[]?` for one result, or `releases[]` (each `{result_id, subpath?, include?, exclude?}`) for several as one job | `job_id`, expected path and rule, file count and size; per release with `releases` |
| `get_jobs` | `status?`, `limit?` | jobs with status, progress, speed and path (active jobs are read from the client directly) |
| `cancel_job` | `job_id` | stops the download; data and links are kept |

Responses are compact JSON; a rejected request (unknown category, invalid sub-path, a pattern
that matches nothing, …) comes back as a tool error with the reason.

### How a search runs

1. `search` takes a category and several title variants (spelling, dashes, original title);
   at most `search.max_query_variants` are used.
2. The first `search.batch_size` sources of the category (or of `sources`, if the agent
   passes them) are searched with every variant in parallel, each request limited to
   `search.source_timeout_sec`.
3. Results are merged — torrents by info hash across sources and variants, keeping every
   source and matched variant — and sorted by source priority, then by seeders. A step
   returns at most `search.max_results` results ("shown N of M").
4. Each source reports `ok`, `empty`, `timeout`, `auth_failed` or `error`. A Prowlarr indexer
   that answers with nothing but is failing (it shows up in Prowlarr's indexer status) is
   reported as `error`, not `empty`.
5. If nothing fits, the agent calls `search_next` for the next batch. Results already shown
   are not repeated.

Results and file lists are cached in SQLite for `search.cache_ttl_hours` and addressed by
opaque `result_id`s; the agent never sees URLs, magnet links or credentials.

### File lists and file selection

`list_files` reads the file list without starting a download: a `.torrent` is fetched through
the Prowlarr proxy and parsed; for magnet-only releases the metadata is fetched from the swarm
(DHT and the magnet's trackers) by an embedded client — nothing is added to qBittorrent. If the
list does not arrive within `files.metadata_timeout_sec`, the answer is `timeout` and nothing
keeps loading in the background.

`download` accepts `include`/`exclude` globs (or exact paths) relative to the result root;
`exclude` applies after `include`. They need the file list, so they are rejected when it could
not be obtained; without them the whole result is downloaded. A pattern that matches no file is
an error. For qBittorrent the selection becomes file priorities set before the torrent starts.

### Soulseek

A Soulseek result is one user's folder: search hits are grouped by user and remote folder
and filtered by the category's `soulseek.extensions`; availability shows the peer's free
upload slot, queue length and speed instead of seeders. A result includes the folder's subfolders (`CD2`,
`Scans`), and disc folders found by the search (`CD1`, `Disc 2`) are grouped into one release.
`list_files` browses the folder (adding the search hits, or relying on them when the peer
cannot be browsed), so covers and booklets can be included or excluded like any other file.
`download` queues only the selected files.

Convy needs **slskd 0.26 or newer**: it queues a download as batches with an explicit
destination, one per subfolder, under `<downloads>/convy/<key>/<folder>/` (the key identifies
the user's folder). The folder keeps its structure, and two folders with the same name (two
`CD2`s, two releases called "Greatest Hits") never clash. `<folder>` is the root replaced by
`subpath`. Convy treats all transfers of one user's folder as one item (`Provider == slskd`,
plus `Username`; there is no `Ratio`, `Tags` or `State`). Downloads started in slskd itself
keep slskd's default layout (`<downloads>/<last remote folder>/`) and are one item per remote
folder. When some files fail for good (rejected, errored, timed out, with no retry pending),
the job fails and lists them; placement does not happen. slskd's downloads directory must be on
the same filesystem as the rule paths, like qBittorrent's. Cleaning up `<downloads>/convy`
after placement is left to you.

Before adding, Convy checks the per-job size limit (`jobs.max_size_gb`) and the free space in
the client's download directory (`jobs.min_free_space_gb` is kept free). Asking the user before
downloading is up to the agent's instructions, not the service.

## Jobs and placement

Downloads started by the agent (see [MCP](#mcp-search-and-download)) become **jobs**. The
download is added to its client right away; once the client finishes, the sync worker
places the files with hard links and the job is `completed` — "files are in place", not
just "the client is done". Seeding continues from the original location.

| Status | Meaning |
| --- | --- |
| `queued` | Added, not downloading yet (client queue, peer queue, fetching metadata) |
| `downloading` | Downloading |
| `stalled` | No progress for `jobs.stalled_after_min` minutes (no seeds, peer offline), or a client problem that may clear (qBittorrent `error` / `missingFiles`); time in a queue does not count |
| `placing` | The client finished; placement waits for a sync cycle or a retry |
| `completed` | Files are at the target path (or left in place, see below) |
| `failed` | Client error, peer refusal, or `jobs.max_placement_attempts` exhausted |
| `cancelled` | Cancelled with `cancel_job`; downloaded data and created links are kept |

A job can hold **several releases**: `download` with `releases` (e.g. three albums the user
asked for at once) starts one job, one `job_id`, instead of one per result. Each release is a
separate download with its own sub-path and selection and is placed on its own as soon as it
finishes; the job combines them:

- **status**: while any release is active the job is active (`downloading` if one downloads,
  else `stalled`, `queued`, `placing`); once all are done it is `failed` if one failed, else
  `cancelled` if one was cancelled, else `completed`;
- **progress and size** add up; the path is the directory holding every release;
- one `job_status` webhook per change of the job's status, not per release; `get_jobs` and the
  web UI list the releases with their own status;
- `cancel_job` stops every unfinished release.

Before anything is added, every release is checked (result, sub-path, duplicates, the size
limit against the total, free space); one problem rejects the whole request. A release the
download client then refuses is reported under `failed` and the job goes on with the rest.

A job may carry a `subpath` chosen by the agent. Where the files go depends on whether a
rule matches (the job's category is visible to the rules as `Category`) and on the sub-path:

| Rule | `subpath` | Result |
| --- | --- | --- |
| matches | given | `rule path / subpath`; the sub-path replaces the download's root folder |
| matches | — | `rule path` with the original structure, as for manual downloads |
| none | given | `save path / subpath`, inside the client's download directory |
| none | — | files stay where they are; the job is completed |

Replacing the root folder: `Movie.2019.2160p.WEB-DL/movie.mkv` with `subpath = "Movie (2019)"`
lands in `<rule path>/Movie (2019)/movie.mkv`; a single-file download goes straight into the
sub-path. Only selected files are linked (qBittorrent files with priority 0 are skipped).

A download belongs to one job at a time: requesting a download that already has an unfinished
job is rejected with that job's id. A download that was placed before (by a rule, or by an earlier
job) is linked again to the new job's target. Paths a client reports that would leave the save
or target directory (`..` in a peer's folder name) are never linked.

A sub-path must be relative, use `/` as separator, contain no `.`/`..`/empty segments, no
control characters and none of `<>:"|?*`, have segments of at most 255 bytes, and stay inside
its base directory after resolving symbolic links. An invalid sub-path is rejected, never
corrected silently. Manual downloads (without a job) are handled exactly as before.

### Immediate placement

The sync interval only decides how quickly finished downloads are placed. To place them
right away, call `POST /sync`: it queues a cycle outside the schedule (a call during a running
cycle starts another one right after it). It fits qBittorrent's *Run external program on
torrent finished* option:

```bash
curl -fsS -X POST http://convy:8080/sync
```

## Health check

`GET /health` reports whether Convy's own SQLite database is reachable and whether the
storage layout allows hard links. It returns `200` with `"status": "Healthy"` when the
database responds and `503` with `"status": "Unhealthy"` when it does not:

```json
{
  "status": "Healthy",
  "checks": [
    { "name": "database", "status": "Healthy", "description": "Database is reachable." },
    { "name": "storage", "status": "Healthy", "description": "Rule paths share a filesystem with the download directories." }
  ]
}
```

Whenever the rules are (re)loaded, Convy checks that every rule path is on the same mount as
every download directory of every client, and that those directories are visible inside the
container. Problems are logged as errors and turn the `storage` check (and the overall
status) into `Degraded`, which still answers `200`.

## Web UI

Convy serves a small web interface at `/`:

- **Overview** — sync state (schedule, last cycle, "Sync now"), every downloader with the
  outcome of its last read, search sources, the storage-layout check, job counts, version.
- **Jobs** — the agent's jobs with live progress, filtered by status; a job with several
  releases unfolds into them; active ones can be cancelled (downloaded data and links are kept).
- **Logs** — the last 5000 log entries kept in memory, followed live, filtered by level and
  text. The full log stays in the console and the log files.
- **Database** — every table read-only, with search, sorting and paging. Values that hold
  secrets are never sent: a search result's content id (it contains the Prowlarr API key)
  is left out, and binary columns (`.torrent` files) are shown only as their size.
- **Webhooks** — every webhook with its events, rule filter and parameters. Webhooks can be
  added, edited, switched off and deleted here; they are stored in the database and used
  together with those from `configuration.yml`, which are shown read-only. Any webhook, saved
  or still in the editor, can be **tested**: Convy sends sample data for the chosen event
  (`linked`, `job_status`, `source_error`) with the webhook's parameters applied and an
  `X-Convy-Test: true` header, and shows the URL, the body sent, the status, the time and
  the answer.
- **Rules** — `rules.yaml` as it is now, with syntax highlighting (YAML and the condition
  language), next to the rules in effect and the properties each one reads. If the file
  cannot be loaded, the error is shown and the previous rules stay in effect. Read-only: edit
  the file to change the rules.

The UI is **off until sign-in is configured**. Users sign in with OpenID Connect; Convy is
tested against Authelia's behaviour, but any provider with the authorization-code flow works.
Only `/`, its assets and `/api/ui` are protected this way: `/mcp` keeps its API key, and
`/sync`, `/health` and `/api/v1` stay behind the IP allow-list alone, so hooks and the agent
need no sign-in. The IP allow-list applies to the UI as well.

| Setting | Purpose |
| --- | --- |
| `UI__OIDC__AUTHORITY` | The provider, e.g. `https://auth.example.com` (Authelia's root URL). |
| `UI__OIDC__CLIENTID` | The client id registered at the provider. |
| `Ui__Oidc__ClientSecret` | The client secret. Pass it as a **Docker secret** with this name. |
| `UI__PUBLICURL` | The address users open, e.g. `https://convy.example.com`. Needed behind a reverse proxy, so the sign-in callback and cookies use the public scheme and host. Convy must be served at the root of that host. |
| `UI__OIDC__ALLOWEDGROUPS__0`, `__1`, … | Optional: only members of these groups get in (others see "Access denied"). Without it any user the provider lets through is accepted. |
| `UI__OIDC__LOGOUTURL` | Optional: where "Sign out" goes after ending Convy's session, e.g. `https://auth.example.com/logout`; Convy adds `rd=<public url>`. Without it only Convy's session ends. |
| `UI__OIDC__SCOPES__0`, … | Optional: scopes to request; default `openid profile email groups`. |
| `UI__OIDC__REQUIREHTTPSMETADATA` | `true` by default. Only turn off to test against a provider on plain HTTP. |
| `UI__AUTH` | `oidc` (default) or `none`: no sign-in at all, the UI is protected by the IP allow-list only. |

These settings are read at startup. The startup log says which mode the UI is in, or why it
is off. The secret in docker-compose:

```yaml
services:
  convy:
    environment:
      UI__PUBLICURL: "https://convy.example.com"
      UI__OIDC__AUTHORITY: "https://auth.example.com"
      UI__OIDC__CLIENTID: "convy"
    secrets:
      - source: convy_oidc_secret
        target: Ui__Oidc__ClientSecret   # read from /run/secrets/Ui__Oidc__ClientSecret

secrets:
  convy_oidc_secret:
    file: ./secrets/convy_oidc_secret
```

### Authelia client

Register Convy in Authelia's `identity_providers.oidc.clients` (Authelia 4.38 or newer):

```yaml
- client_id: 'convy'
  client_name: 'Convy'
  client_secret: '$pbkdf2-sha512$310000$...'   # digest of the secret given to Convy
  public: false
  authorization_policy: 'two_factor'
  redirect_uris:
    - 'https://convy.example.com/signin-oidc'
  scopes: ['openid', 'profile', 'email', 'groups']
  response_types: ['code']
  grant_types: ['authorization_code']
  require_pkce: true
  pkce_challenge_method: 'S256'
  token_endpoint_auth_method: 'client_secret_post'   # required: Convy sends the secret in the body
  consent_mode: 'pre-configured'
  pre_configured_consent_duration: '1 month'
```

Generate the secret and its digest with
`authelia crypto hash generate pbkdf2 --variant sha512 --random --random.length 72 --random.charset rfc3986`:
the random password goes into Convy's Docker secret, the digest into `client_secret`.

Things that commonly go wrong:

- **`token_endpoint_auth_method`** must be `client_secret_post`. Authelia's default for a
  confidential client is `client_secret_basic`, and the sign-in then fails with
  `invalid_client`.
- **The redirect URI** must match exactly. It is `<UI__PUBLICURL>/signin-oidc`; without
  `UI__PUBLICURL` behind a proxy Convy would build an internal `http://…` address.
- **Groups**: Authelia 4.39+ puts groups and the user name only into the userinfo response;
  Convy reads them from there, nothing to configure.
- **Sign-out**: Authelia has no OIDC end-session endpoint. Set `UI__OIDC__LOGOUTURL` to
  `https://auth.example.com/logout` to end the Authelia session too (Authelia only follows
  `rd` to an https address inside its cookie domain).
- **Convy must reach Authelia by its public URL** (discovery, keys, token exchange) and
  trust its certificate. Inside Docker, give the reverse proxy a network alias for the
  Authelia host name.

Sessions last 12 hours (sliding). The keys that protect the session cookie are stored next
to the database in `keys/` (e.g. `/var/lib/convy/keys`), so keep that directory on the
volume or every restart signs everyone out.

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
      # Optional — matches the default baked into the image; override to relocate the db.
      ConnectionStrings__SQLite: "Data Source=/var/lib/convy/convy.db"
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
    healthcheck:
      # Probes /health, which reports unhealthy only when the SQLite database is
      # unreachable (see Health check above). NOTE: the base aspnet image ships no
      # curl/wget — either bake one in (RUN apt-get update && apt-get install -y curl)
      # or drop this block and probe /health from an external monitor instead.
      test: ["CMD", "curl", "-fsS", "http://localhost:8080/health"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 10s

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
- The `healthcheck` probes from inside the container (loopback), so it passes the IP
  allow-list with the built-in defaults. Remove the block if you don't add curl to the image.

## Building and testing

Requires the .NET 10 SDK.

```bash
dotnet build Convy.slnx
dotnet test Convy.slnx
```

The web UI lives in `frontend/` (React, TypeScript, Vite) and needs Node.js 24. `npm run build`
writes it into `src/Convy/wwwroot`, where Convy serves it from; the Docker image builds it in
its own stage. For UI work, run Convy (`dotnet run --project src/Convy`) and the Vite dev
server, which forwards the API to it:

```bash
cd frontend
npm ci
npm run dev
```

The expression language is generated from an ANTLR grammar at build time; the build task
downloads a JRE automatically the first time, so no separate Java installation is needed.

## Project layout

| Project | Responsibility |
| --- | --- |
| `Convy` | ASP.NET host: the polling worker, dependency injection, HTTP endpoints |
| `frontend` | the web UI (React + Vite), built into `src/Convy/wwwroot` |
| `Convy.Services` | downloaders (qBittorrent behind `IDownloader`), sync cycle, file linking, state tracker, webhook notifier |
| `Convy.Sources` | search sources (Prowlarr), torrent metadata (.torrent parsing, magnet metadata from DHT), shared contracts |
| `Convy.Mcp` | MCP tool definitions, a thin layer over the services |
| `skills/convy` | the agent skill: how to search, choose releases and download through the MCP tools |
| `Convy.PathExpressions` | the rule language: ANTLR grammar, expression tree over item properties, mapping-file loader |
| `Convy.Data` | EF Core (SQLite) entities and migrations |
| `Convy.Infrastructure` | low-level helpers (the native hard-link wrapper) |
| `Tests/*` | unit tests for the rule language and the state tracker |
