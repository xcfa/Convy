# TODO

## Processing handed-off jobs without harming the seeding files

Jobs with an instruction are hard-linked into `jobs.processing_path/<job_id>/` for an agent to
process. These are hard links to the files the client seeds: a tool that rewrites a file in
place (tag editors, beets with `write: yes`) changes the seeding copy too, and the client then
reports the torrent as damaged. Today the agent is only told (README) to copy what it changes.

Think through how Convy could guarantee it, e.g.:

- give the agent copies instead of links (costs space until the job is processed), or reflinks
  where the filesystem supports them (Btrfs, XFS);
- or let the agent write only into `destination`, then replace every file there that is
  byte-identical to a downloaded one with a hard link to it, so unchanged files cost no space
  and changed ones stay copies;
- clean up the processing folder once the agent reports the job done.
