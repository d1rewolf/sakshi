# Releasing sakshi

A release is a pushed tag. The tag is the version: nothing in the source is
bumped. The workflow `.github/workflows/release.yml` packs the three installers
with Velopack and uploads them to one GitHub Release.

## Cut a release

1. Make sure `main` has the commit you want and its CI run is green.
2. Tag and push:

   ```sh
   git switch main && git pull
   git tag v0.1.0 && git push origin v0.1.0
   ```

   `v0.1.0` releases version `0.1.0`. Any `v*` tag publishes, so do not push
   a tag you do not mean.
3. Watch it: `gh run watch` (pick the `release` run).

## What happens

- Three jobs pack in parallel: Linux (`linux-x64`), macOS (`osx-arm64`) and
  Windows (`win-x64`). Each keeps its output as a run artifact.
- One `upload` job then runs once, after all three have packed, and uploads
  in turn Linux, macOS, Windows to the same Release. The first two leave it as
  a draft; the Windows upload (last) publishes it.
- The Release holds, for version `<v>`:
  - Linux: `sakshi.AppImage`, `sakshi-<v>-linux-full.nupkg`, `releases.linux.json`
  - macOS: `sakshi-osx-Setup.pkg`, `sakshi-osx-Portable.zip`,
    `sakshi-<v>-osx-full.nupkg`, `releases.osx.json`
  - Windows: `sakshi-win-Setup.exe`, `sakshi-win-Portable.zip`,
    `sakshi-<v>-full.nupkg` (no channel in the name: `win` is Velopack's
    default channel there), `releases.win.json` and `RELEASES` (Velopack's
    legacy feed, uploaded for the `win` channel only)

  The download links in `README.md` and `docs/install.md` use
  `releases/latest/download/<file>`, so they follow each new release; the
  installer file names carry no version and must stay that way.
  Each Release also carries `ffmpeg-<version>.tar.xz`, the source of the
  ffmpeg the installers ship (the LGPL asks for it to be offered).
  Users only need the three installers (`Setup.exe`, `Setup.pkg`,
  `.AppImage`); the rest is Velopack's package and feed files. The runners
  pack from an empty output folder, so there are no `-delta.nupkg` files.

## Dry run (no release)

Run the workflow from the Actions tab (or
`gh workflow run release.yml --ref <branch>`). It packs with version
`0.0.1-dryrun.<run number>` and uploads nothing to Releases; the installers are
artifacts on the run.

## If an upload fails part-way

A failure after the first upload leaves a **draft** Release with only some of
the installers. Delete it, then re-run the failed workflow run
(`gh run rerun <id>`, or "Re-run failed jobs" on GitHub):

```sh
gh release delete v0.1.0 --yes
```

The tag stays, which is what you want. Check the Release page afterwards and
that all three installers are there.

If the release itself is wrong (not an upload failure), delete the Release and
the tag (`gh release delete v0.1.0 --yes --cleanup-tag`), fix, and tag again.

## Pointing people to it

Link to `docs/install.md` or the Releases page. Nothing in the app contacts
GitHub, so updates are installed by downloading the new release.

## Not signed

The builds are not signed or notarised, so Windows and macOS warn on first
launch. `docs/install.md` explains how to get past that.

## Guides as PDFs

`packaging/make-guides.py` turns `docs/install.md` and `docs/user-guide.md`
into one PDF per system (install steps for that system only, then the user
guide), for anyone who wants the guide offline:

```sh
uv run --with weasyprint==70.0 packaging/make-guides.py ~/Downloads/sakshi-guides
```

It needs `pandoc`. Rebuild the PDFs whenever either guide changes.
