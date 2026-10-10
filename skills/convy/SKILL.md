---
name: convy
description: Find and download movies, series, music and other media through the Convy MCP server (get_categories, search, search_next, list_files, download, get_jobs, cancel_job), pick releases by the user's quality preferences and report download progress. Use whenever the user asks to find, download or check on a film, show, album or other media, or mentions Convy, trackers, torrents or Soulseek.
---

# Convy: finding and downloading media

Convy searches trackers (through Prowlarr) and Soulseek, downloads with qBittorrent or slskd
and, once a download finishes, hard-links the files into the media library by its own rules.
**Choosing the release is your job**; Convy does not judge release names. You never see URLs,
magnets or credentials, only opaque ids (`r_…` results, `s_…` searches, `j_…` jobs).

Talk to the user in their language. Show releases as short readable lines, never raw JSON.

## Workflow

1. **Category.** Call `get_categories` once per conversation. Pick the category that fits the
   request (movies, series, music, …); ask only if two fit equally. Remember its `path_hint`.
2. **Queries.** Call `search` with the category and up to 3 title variants, for example the
   original title, the Russian title and an alternative spelling (`Spider-Man` / `Spiderman`).
   Trackers list both titles, so one of them usually hits. Add the year only when the title
   is generic. For music: `Artist Album`. For a series, search the show, not one episode.
3. **Read the answer.**
   - `sources[].status`: `ok` / `empty` are normal; `timeout`, `auth_failed`, `error` mean a
     source did not answer. Mention it in one line if it may hide the release.
   - Nothing suitable and `has_more: true` → `search_next` with the `search_id`.
   - `note` says results were dropped ("Showing N of M") → narrow the queries.
4. **Choose** by the preferences below. Look at the title, `size_bytes`, `file_count` and
   `availability` (torrents: `seeders`; Soulseek: `free_slot`, `queue_length`,
   `speed_bytes_per_sec`). A torrent with 0 seeders will most likely stall.
5. **Check the files** with `list_files` when the structure matters: season packs, multi-disc
   albums, discographies, releases with extras, or when you want to download only part of
   it. Without `path` you get the top level with a summary per folder; `path` opens one
   folder; `glob` (`**/*.mkv`) lists matching files. `status: timeout` means the file list
   could not be fetched (magnet without reachable peers): take another result, or download
   this one whole (include/exclude are then impossible).
6. **Confirm.** Present your pick in one or two lines (title, quality, audio, size, why) plus
   at most two alternatives, and ask to confirm. Never call `download` without the user's
   confirmation. One confirmation may cover several downloads the user asked for together.
7. **Download** with `download(category, result_id, subpath, include?, exclude?)`, see
   [Sub-path](#sub-path) and [File selection](#file-selection). When the user asked for
   several things of one category at once (three albums, two seasons from different
   releases), pass them together as `releases: [{result_id, subpath, include?, exclude?}, …]`:
   that makes **one** job with one status and one notification instead of one per result.
   Use separate calls for different categories. Releases a client refused come back under
   `failed`; tell the user. Report `job_id`,
   `expected_path` (a forecast; rules are evaluated again at placement) and `rule`. Without a
   rule (`rule: null`) the files stay in the client's download folder; say so.
8. **Follow up** with `get_jobs` when the user asks, or once right after downloading to see
   that it started. Do not poll in a loop.

## Release preferences

These are the user's defaults. Pick by them without asking; ask only when they cannot be met
or the choice is genuinely ambiguous (theatrical vs. extended cut, several editions).

### Movies and series

1. **Russian audio track is required.** Markers in titles: `Dub`, `MVO`, `DVO`, `AVO`, `VO`,
   `Rus`, `RUS`, `Дубляж`, `Многоголосый`. Say which kind it is when presenting (Dub, MVO …).
   Some releases keep Russian audio as separate files (`RUS Sound/*.mka`, `*.ac3`): never
   exclude those folders.
2. **2160p with HDR** (`HDR`, `HDR10`, `HDR10+`, `Dolby Vision`, `DV`). If the only HDR
   option is Dolby Vision without HDR10 (`DV` alone, often WEB-DL "P5"), mention it: it
   plays with wrong colours on players without Dolby Vision.
3. **BDRip over BDRemux.** Prefer encodes (`BDRip`, `UHD BDRip`), then `WEB-DL` / `WEBRip`.
   Take `BDRemux` / `Remux` and full discs (`BDMV`, `ISO`, `Blu-ray Disc`) only when nothing
   else fits. A single 2160p film above ~50 GB is almost always a remux or a disc even if
   the title does not say so; a 2160p encode is usually 10–35 GB.
4. More seeders win between otherwise equal releases.

If no release has both Russian audio and 2160p HDR, do not downgrade silently: show the best
options (e.g. 2160p SDR or 1080p with Russian audio, 2160p HDR without it) and ask.

Series: prefer complete season packs. To get some seasons of a bigger pack, check the folder
names with `list_files` and use `include` (see below).

### Music

1. **Lossy is preferred**: MP3 320 kbps or V0, AAC 256+, Opus. Take FLAC or other lossless
   only when there is no decent lossy release, or the user asks for it. Avoid bitrates
   below 192 kbps unless nothing else exists.
2. The right edition: the album the user named, not a compilation or a discography, unless
   asked. For a discography torrent, download only the album with `include`.
3. Soulseek results are one user's folder (`Artist / Album [mp3] — user`). Prefer
   `free_slot: true`, a short queue and higher speed; check that `file_count` matches the
   tracklist. When a folder mixes formats, `include: ["**/*.mp3"]`.

### Other categories

Books, audiobooks, software and the `other` category have no fixed preferences: pick the most
complete, well-seeded release and say why.

## Sub-path

`subpath` is the folder the release lands in, relative to the rule's library folder; it
replaces the release's own root folder (`Dune.2021.2160p.BDRip/` → `Dune (2021)/`). Build
it from the category's `path_hint` with real values:

- Movies, `Title (Year)` → `Dune (2021)`. Use the original title as on TMDB/IMDb and the
  release year; media servers match on them. Use another title only if the user asks.
- Music, `Artist/Year - Album` → `Evanescence/2003 - Fallen`.
- Series: follow the category's `path_hint`; the year is the first air year.

Rules: relative, `/` as separator, no `..`, no empty segments, none of `<>:"|?*`. Replace
`:` in titles with ` -` (`Mission - Impossible (1996)`). An invalid sub-path is rejected,
never corrected for you. Omit `subpath` only when the category has no `path_hint`.

## File selection

`include` / `exclude` take globs or exact paths relative to the result root, as `list_files`
shows them; `exclude` applies after `include`. Without them everything is downloaded.

- One season of a pack: `include: ["Season 02/**"]` (use the real folder name).
- One album of a discography: `include: ["2003 - Fallen/**"]`.
- Skip samples: `exclude: ["**/*sample*"]` when `list_files` shows them.

A pattern that matches no file is an error: check the names with `list_files`.

## Jobs

| Status | Meaning | What to tell or do |
| --- | --- | --- |
| `queued` | Added, waiting (client queue, peer queue, metadata) | Normal at the start |
| `downloading` | In progress | Report `progress` and speed |
| `stalled` | No progress for a while (no seeds, peer offline) | Offer to wait, pick another result, or cancel |
| `placing` | Downloaded, waiting for placement | Normal; takes up to one sync cycle |
| `completed` | Files are in the library at `path` | Done |
| `failed` | Client error, peer refusal, placement failed | Quote `error`; offer another result |
| `cancelled` | Cancelled by `cancel_job` | — |

A job with several releases shows them under `releases`, each with its own status; the job
is `completed` once all are placed, `failed` if one failed. `cancel_job` only when the user
asks; it stops every unfinished release and keeps data and links.

## Errors

Rejected calls return a tool error with the reason. The common ones:

| Message | Do |
| --- | --- |
| `…is already job j_N…` | The same release is already downloading: show that job (`get_jobs`) |
| `…above the N GiB limit per job` / `Not enough free space…` | Tell the user; offer a smaller release |
| `Result '…' is unknown or expired` / `Search '…' is unknown or expired` | Ids live a few hours: search again |
| `The file list … could not be obtained in time…` | Download without include/exclude, or pick another result |
| `No source is available for category …` | Sources are down or not configured; `get_sources` shows which |
| sub-path errors | Fix the sub-path by the rules above and retry |

Do not retry a failing `download` blindly; read the reason first.
