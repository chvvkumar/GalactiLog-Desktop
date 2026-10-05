# Packaging with Velopack

This document describes how to build, pack, install, and uninstall a Velopack release of
GalactiLog on Windows, and how to run the application without an installer for local
verification. Section 5 is the packaging verification bar: a verification run follows its
numbered checks in order and records each result.

## 1. Prerequisites

- .NET SDK 10.0.4xx. See `docs/superpowers/HANDOFF.md` section 3.1 item 2 for installation.
- The Velopack CLI (`vpk`) installed as a global tool, pinned to version 1.2.0, matching the
  `Velopack` package version in `Directory.Packages.props`:

  ```
  dotnet tool install -g vpk --version 1.2.0
  ```

  See `docs/superpowers/HANDOFF.md` section 3.1 item 7 for the install location and the reason
  for the pin.

### The `DOTNET_ROOT` rule

`vpk`'s apphost resolves the .NET runtime through the `DOTNET_ROOT` environment variable or the
machine-wide install at `C:\Program Files\dotnet`. It never resolves the runtime through `PATH`.

- On a machine whose SDK came from `winget install Microsoft.DotNet.SDK.10`, the runtime is
  already at `C:\Program Files\dotnet`. No `DOTNET_ROOT` is needed for any `vpk` command.
- On a machine whose SDK is a user-profile install (`%LOCALAPPDATA%\Microsoft\dotnet`), every
  `vpk` command needs `DOTNET_ROOT` set for that one command, or it fails with a message that the
  framework was not found.

  Git Bash:

  ```
  DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet" vpk pack ...
  ```

  PowerShell, inside a throwaway shell:

  ```
  $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
  vpk pack ...
  ```

**Never persist `DOTNET_ROOT` at user or machine scope.** A persisted `DOTNET_ROOT` pointing at a
user-profile install breaks every other .NET application on the machine. Scope it to the single
command, in the same line or the same throwaway shell, and nothing else.

Launching the packaged `GalactiLog.exe` directly follows the same rule as `vpk`, because it is
also an apphost. `dotnet run` needs none of this, because `dotnet` itself resolves the runtime
through `PATH`.

## 2. Publish

From the repository root, with a user-profile SDK:

```
export PATH="/c/Users/<user>/AppData/Local/Microsoft/dotnet:$PATH"

dotnet publish src/GalactiLog.App -c Release -r win-x64 --self-contained \
  -o publish/win-x64 -p:Version=0.0.1-local.1
```

On a machine with the SDK at `C:\Program Files\dotnet`, drop the `PATH` line.

`0.0.1-local.1` is a synthetic version for a local pack: a valid semantic version that sorts
below every real tag, with a prerelease segment that marks it as synthetic. It cannot collide
with a version produced by the release workflow.

`-o publish/win-x64` is required. Without an explicit output directory, `dotnet publish` uses the
SDK's default layout, which the pack step in `.github/workflows/release.yml` would then have to
guess at separately. Pinning both the publish output and the pack input to the same literal path
removes that guess.

`-p:SourceRevisionId=<sha>` is optional only when the publish runs inside a git checkout. There the
SDK's source control integration sets `SourceRevisionId` from the working copy's git HEAD, so a
publish that passes nothing still receives a SHA. A publish from a copy without `.git`, which is
what a verification clean copy is, receives none and must pass the flag explicitly to produce an
artifact whose SHA can be checked (section 5 check 13). The release workflow passes
`-p:SourceRevisionId=${{ github.sha }}` explicitly so the shipped artifact's SHA does not depend
on that SDK default. Either way, the value reaches
`AssemblyInformationalVersionAttribute` as `+<sha>`, and `BuildInfo.GitSha` reads the substring
after the first `+`. The field reads `unknown` only when the informational version carries no `+`
segment, which happens for a publish run outside a git checkout or with SourceLink disabled.

`publish/win-x64` is repository-relative and is listed in `.gitignore`. It is build output, not a
tracked path.

## 3. Pack

```
DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet" vpk pack \
  --packId GalactiLog \
  --packVersion 0.0.1-local.1 \
  --packDir publish/win-x64 \
  --icon src/GalactiLog.App/Assets/GalactiLog.ico \
  --mainExe GalactiLog.exe \
  --channel alpha
```

On a machine with the SDK at `C:\Program Files\dotnet`, drop the `DOTNET_ROOT=` prefix.

`--icon` sets the icon on Setup.exe, the shortcuts and the Add or Remove Programs entry.
GalactiLog.exe carries its own icon from the csproj's `ApplicationIcon`.

| Flag | Value | Reason |
| --- | --- | --- |
| `--packId` | `GalactiLog` | The package id, `design-spec.md` 17.1. |
| `--packVersion` | The derived version, or a synthetic one for a local pack | `design-spec.md` 17.4. |
| `--packDir` | `publish/win-x64` | The publish output directory. Must be the same literal path used for `dotnet publish -o`. |
| `--mainExe` | `GalactiLog.exe` | The assembly name set by the csproj's `AssemblyName`. |
| `--channel` | `alpha`, `rc`, or `stable` | The branch-to-channel mapping in `design-spec.md` 17.4. |

Do not add `--framework`. The publish is self-contained, so the package carries its own runtime;
a framework dependency declaration would be wrong.

Do not add `--delta` or `--noDelta`. Delta packages are on by default and are the reason
`design-spec.md` 17.1 lists Velopack as a dependency.

Do not add `--icon`. The executable already carries `Assets\GalactiLog.ico` through the csproj's
`ApplicationIcon`. A second icon source is a second thing to keep in step with the first.

## 4. What is produced

`vpk pack` writes a `Releases` directory inside the working directory it runs from. The
directory holds, by name pattern for pack id `GalactiLog` and channel `alpha`:

- A setup executable, named with the channel: `GalactiLog-alpha-Setup.exe`.
- A full package: `GalactiLog-<version>-alpha-full.nupkg`.
- A portable zip: `GalactiLog-alpha-Portable.zip`.
- A channel manifest: `RELEASES-alpha`. The installed application reads this file to find
  updates.
- Two small JSON metadata files, `assets.alpha.json` and `releases.alpha.json`.

A delta package (`GalactiLog-<version>-alpha-delta.nupkg`) appears only when a prior full package
for the same channel already exists in the `Releases` directory; a first pack for a channel
produces none.

Velopack 1.2.0 is the version that produces this list. Record the exact file names observed on
each run rather than assuming the pattern; that observed list is the record of record.

`Releases` is listed in `.gitignore`. It is build output, not a tracked path.

## 5. Install, launch, uninstall

This section is the numbered verification procedure. Each check has one observable result.

### Before starting

Installing writes to the real, per-user `%LOCALAPPDATA%\GalactiLog`. Before check 7, record
whether `%LOCALAPPDATA%\GalactiLog`, `%LOCALAPPDATA%\GalactiLogData` and
`%APPDATA%\GalactiLog\datapath.json` exist. An install and an uninstall touch the first and must
not touch the second or the third. If the first exists, do not proceed on this machine: a test
install would replace a real installation, and the uninstall in check 14 removes that root in
full. If the second or the third exists, the machine holds a real library: run the procedure in a
fresh Windows user profile or a virtual machine instead, and record which was used.

`GALACTILOG_APPDATA` does not redirect where Velopack installs the application. It redirects only
the application data root the running process resolves (`AppHost.Build`, see
`docs/superpowers/HANDOFF.md` section 3.5). Setting it before launch keeps the database and logs
out of the real app data root, but `current\` still installs under `%LOCALAPPDATA%\GalactiLog`
regardless of the variable. This is the trap in this procedure: setting the variable does not make
the install itself safe to run against a real profile. The variable also suppresses the data
location pointer (`design-spec.md` 17.2), so a run that sets it never reads or writes the real
`%APPDATA%\GalactiLog\datapath.json` and never relocates a real library.

### The checks

1. `dotnet publish` exits 0 with 0 warnings. `TreatWarningsAsErrors` is set, so any warning is
   already a build error.
2. `publish/win-x64/GalactiLog.exe` exists. The assembly name is `GalactiLog`, not
   `GalactiLog.App`.
3. `publish/win-x64/Catalogs/` exists and is non-empty.
4. The publish is self-contained: `publish/win-x64` contains the .NET runtime assemblies
   (`System.Private.CoreLib.dll` among them) and the SkiaSharp native library
   (`libSkiaSharp.dll`).
5. `vpk pack` exits 0.
6. `Releases/` contains the files named in section 4 above. Record the exact names observed.
7. Run the setup executable. It installs without elevation, into a per-user location, and does
   not prompt for administrator rights.
8. `%LOCALAPPDATA%\GalactiLog\current\GalactiLog.exe` exists after the install. This is the
   Velopack binary layout described in `design-spec.md` 17.2.
9. `%LOCALAPPDATA%\GalactiLog\current\Catalogs\` exists after the install.
10. The application launches from the installed location and its window opens, with no
    `DOTNET_ROOT` set. The packaged build is self-contained and carries its own runtime; needing
    `DOTNET_ROOT` here would mean the publish was not self-contained, which check 4 already
    covers.
11. `%LOCALAPPDATA%\GalactiLogData\galactilog.db` is created, and
    `%LOCALAPPDATA%\GalactiLogData\logs\` contains a `galactilog-*.log` file with the
    unconditional startup line. That line names the version, the app data root, where the root
    came from, and `cliMode false`, and is the fastest confirmation that the installed build is
    the one just packed.
12. The setup wizard appears on first run, because `general.setup_complete` is false on a fresh
    app data root. Cancel or complete it; either outcome satisfies this check.
13. The About tab shows the synthetic version and the `alpha` channel. Record the About tab's SHA.
    On a publish made inside a git checkout, it equals `git rev-parse HEAD` of the checkout that
    was packed. On a clean copy, which carries no `.git` and therefore gives the SDK no SHA to
    read, pass `-p:SourceRevisionId=<sha of the commit copied>` to the publish in section 2,
    exactly as `.github/workflows/release.yml` does, and compare the About tab to that value. A
    publish outside a checkout that passes nothing reports `unknown`, which is correct and proves
    nothing about the packed commit.
14. Uninstall through Windows Settings, Apps, or the uninstaller Velopack registers.
15. Confirm the Velopack hooks ran and that the data root survived. Record all three
    observations: `%LOCALAPPDATA%\GalactiLog` is gone and the application is removed from the
    Apps list; `%LOCALAPPDATA%\GalactiLogData` is still there with `galactilog.db` inside it;
    `%APPDATA%\GalactiLog\datapath.json` is still there. This is the check that proves the data
    root sits outside the install root (`design-spec.md` 17.2). It exists because of the
    behaviour before the relocation: observed 2026-09-15 on 1.0.0-alpha.1, whose data root was
    still inside the install root, the uninstall removed `%LOCALAPPDATA%\GalactiLog` in full,
    database, logs and thumbnail cache included, and removed the Start menu entry and the HKCU
    uninstall entry with it. Record the outcome again on each run rather than assuming it. Do not
    add an uninstall hook that deletes the database; that decision belongs to the user, not to
    this procedure.

## 6. Cleanup

Remove a test install so the developer's own `%LOCALAPPDATA%\GalactiLog` is not left in an
installed state:

1. Uninstall through Windows Settings, Apps, or the registered uninstaller (check 14 above).
2. Confirm `%LOCALAPPDATA%\GalactiLog\current` is gone.
3. The uninstall in step 1 removes `%LOCALAPPDATA%\GalactiLog` in full, and from this version
   that directory holds no user data. Do not delete `%LOCALAPPDATA%\GalactiLogData` or
   `%APPDATA%\GalactiLog\datapath.json` by hand unless the "Before starting" record in section 5
   shows neither existed before the run. A root that held pre-existing data before the test
   install is never deleted by hand.
4. Delete the local build output: the `publish/` and `Releases/` directories under the repository
   root, or wherever `--packDir` and the default `Releases` output pointed if a different location
   was used.

Nothing in this procedure or in the application deletes a user file on its own; every removal
above is either the operating system's uninstaller or a manual step performed by the person
running the test.

## 7. The local-run alternative

When only the application, not the installer, needs to be under test, run it without publishing
or packing:

```
export GALACTILOG_APPDATA="C:/tmp/galactilog-verify"
dotnet run --project src/GalactiLog.App -c Release
```

`GALACTILOG_APPDATA` points the application at a throwaway app data root, so the real
`%LOCALAPPDATA%\GalactiLogData` and the data location pointer are untouched. See
`docs/superpowers/HANDOFF.md` section 3.5 for the
full set of run and shell variants, including the PowerShell and `cmd` forms. Do not set
`GALACTILOG_APPDATA` to a drive root.

This path exercises the running application only. It does not exercise the setup executable, the
per-user install location, or the uninstall hooks; those require the full procedure in section 5.

## 8. The local build script

`tools/Build-GalactiLog.ps1` runs sections 2 and 3 of this document and then either copies the
setup executable to a folder or installs it silently. It resolves the SDK and `vpk` locations
itself and scopes `DOTNET_ROOT` to the `vpk` call inside its own process.

```
.\tools\Build-GalactiLog.ps1                          # build, copy the installer to Downloads
.\tools\Build-GalactiLog.ps1 -Install                 # build and install silently
.\tools\Build-GalactiLog.ps1 -Clean App -Install      # uninstall first, keep the data root
.\tools\Build-GalactiLog.ps1 -Clean All -Install      # uninstall and delete the data root and pointer
```

`-Clean All` prints every path it will delete and deletes them without asking. `-Force` is
accepted and ignored. `-OutputPath`, `-Version`, `-Channel`, and `-Configuration` override
the defaults. Run `Get-Help .\tools\Build-GalactiLog.ps1 -Full` for the parameter list.

`-SeedLocation` runs after delivery. It reads `lat`, `lon` and `fixStatus` from the JSON at
`-GpsUrl` (default `http://espgps.lan/api/status`), looks the time zone up from the
coordinates, and writes `observer_latitude`, `observer_longitude` and `observer_timezone` into
`user_settings.general`, so the setup wizard opens with the Location step prefilled. It first
runs `GalactiLog.exe scan --quiet` (the installed build with `-Install`, otherwise the published
one) so the database and its schema exist, then `dotnet run tools\Seed-Location.cs`, a .NET 10
file-based program that pulls Microsoft.Data.Sqlite and GeoTimeZone from nuget.org. It fails
without a GPS fix and changes nothing else in the settings row.

```
.\tools\Build-GalactiLog.ps1 -Clean All -Install -SeedLocation   # wipe, install, prefill the location
```

The script packs into `Releases\local`, not `Releases`, so a local pack never produces a delta
against a release package that happens to be in `Releases`.
