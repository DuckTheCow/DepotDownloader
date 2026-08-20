# `-applist` (local patch, not upstream)

This patch was written by an AI. It has not been reviewed line by line by
a human, and has not been built or run by the AI that wrote most of it. It
is an unreviewed, untested modification, made for one person's private use,
not maintained software. Do not redistribute it. Do not rely on it for
anything that matters. There is no guarantee it works, no warranty, and no
support. It may produce incorrect output, misbehave, or affect the standing
of the Steam account it runs under, in ways not yet identified. Read the
diff before using it.

## Why

DepotDownloader normally takes one `-app` per process, and each process
performs its own Steam logon. Fetching manifests for a large library (SteamRE
issue #674 reports rate-limiting after ~110 requests, with roughly an hour-long
block) means one logon per app, which Steam blocks well before a library of
any real size finishes.

`-applist` logs in **once** and downloads every app in the list during that
single authenticated session.

## What it does

- Add `-applist <file>` as an alternative to `-app <id>`.
- If both `-app` and `-applist` are given, `-applist` wins and a warning is
  printed.
- `InitializeSteam` / `ShutdownSteam3` still run exactly once, wrapping the
  whole batch — not once per app.
- When more than one app is being processed, each app is written to
  `<dir>/<appid>` (where `<dir>` is your `-dir` value, or `depots` if you
  didn't pass one). With a single app (plain `-app`, or an `-applist` with
  only one entry), output goes straight to `<dir>` as before — behavior for
  the single-app case is unchanged.
- Apps that fail (delisted, unlicensed, region-locked, or otherwise) are
  skipped with a message; the run continues with the rest of the list. A
  summary line at the end reports how many succeeded/failed and how long the
  batch took. Exit code is `1` only if every app in the list failed.
- When batching, a `<dir>/success.txt` file tracks which app IDs have
  already completed successfully. On startup, any app already listed there
  is skipped entirely — before any Steam API call is made for it, not just
  before the file download. This matters for resuming a large, interrupted
  batch: even when a manifest is already cached on disk, DepotDownloader
  still has to call Steam for that app's app info and every depot's
  decryption key before it can check the local cache (this is normal
  DepotDownloader behavior, not specific to this patch). Re-running a batch without
  `success.txt` support would repeat those calls for every already-done app
  every time; `success.txt` skips them outright. Each app is appended to
  `success.txt` the moment it finishes successfully (whether its manifest
  was freshly downloaded or already on disk), so a first pass over an
  already-fully-cached directory will populate the file, and every pass
  after that gets faster and lighter on Steam's API.
- When batching, Ctrl+C doesn't kill the process immediately. It finishes
  the app currently in progress (so that app's `success.txt` entry, if it
  succeeds, isn't lost), prints a summary of what was done this run, and
  exits. Press Ctrl+C a second time to force an immediate exit instead.
  This is meant for the "got throttled, stop, wait it out, resume" workflow:
  run the batch, Ctrl+C when Steam starts rejecting requests, come back
  later and run the exact same command again — already-completed apps skip
  instantly via `success.txt` and it picks back up on the rest of the list.
- A single depot manifest download retries up to 20 times before giving up
  on that depot (previously unbounded, so a sustained error like a CDN
  returning ServiceUnavailable repeatedly could retry forever). This does
  not change retry behavior for normal transient errors, it only stops an
  indefinite retry loop. When the cap is hit, that app is marked failed and
  is not added to `success.txt`, the same as any other failure.
- If 5 apps in a row fail (a constant in `Program.cs`, not a command-line
  option), the batch assumes Steam is throttling the session and stops
  itself the same way Ctrl+C does: finish cleanly, keep `success.txt`,
  print a message, exit. Re-run the same command later to continue.

## File format

One app ID per line.
- Blank lines and lines starting with `#` are ignored.
- If a line contains commas, only the first field is used, so a raw Steam
  library CSV export (`appid,name,...`) works directly — the header row is
  skipped automatically because it doesn't parse as a number.
- Duplicate IDs are removed.

Example `apps.txt`:

```
# my library
1007
232250
440,Team Fortress 2
```

## Worked example

```
DepotDownloader -applist apps.txt -manifest-only -os windows -language english -dir depots
```

This produces, for each app ID in `apps.txt`:

```
depots/<appid>/manifest_<depotid>_<manifestid>.txt
```

authenticating with Steam exactly once for the whole batch, regardless of how
many apps are in the file.

## Caveat

This is a local patch on top of upstream `SteamRE/DepotDownloader`, not an
accepted or merged feature. It has not been submitted upstream. The open
upstream PR #707 is unrelated — it allows multiple `-manifest` values for a
single `-depot` (many versions of one game), not many apps under one login.
