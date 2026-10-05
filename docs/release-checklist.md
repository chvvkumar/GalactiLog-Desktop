# Release checklist

This checklist assumes the reader has completed `docs/superpowers/HANDOFF.md` section 3 and has
nothing else open. It does not restate that setup.

## 1. Before you start

- `git status` on `snd` is clean.
- `dotnet build -c Release --no-incremental` and `dotnet test -c Release` are green with 0
  warnings, and the test count printed by `dotnet test` matches the last figure in
  `docs/superpowers/TRACKING.md` section 6.
- The working tree is the two-clone layout described in `docs/superpowers/HANDOFF.md`
  section 3.2: this repository and a sibling clone of the web repository.
- Push access to `chvvkumar/GalactiLog-Windows`.
- Sections 3, 4, and 7 use the GitHub CLI (`gh`), authenticated against this repository. Install
  it once with `winget install GitHub.cli`, then run `gh auth login`.
  `docs/superpowers/HANDOFF.md` section 3 does not install `gh`, because the build and test
  procedure it describes never needs it. Nothing in section 2 needs `gh`.
- `dev` and `main` exist on `origin`. Check with `git ls-remote --heads origin` and confirm both
  `refs/heads/dev` and `refs/heads/main` are listed alongside `refs/heads/snd`. If either is
  missing, the repository owner creates them once before section 3 or section 4 can be followed;
  see the precondition at the start of section 3.
- Nothing below requires the `vpk` tool on the local machine. CI packs the release. The local
  publish and pack procedure in `docs/packaging.md` is for verifying a build before or after a
  release, not for cutting one.
- The repository secret `GEMINI_API_KEY` exists (`gh secret set GEMINI_API_KEY`). The release
  workflow and the pull request description workflow both read it to write prose from the commit
  messages. Without it both still succeed: the release notes are the raw commit list and a pull
  request keeps the title and description its author wrote.

## 2. Cutting an alpha from `snd`

This is the ordinary case, and the only one that happens during development.

1. Refresh `docs/reference/repomix/galactilog-windows.xml` with the commands in `docs/reference/README.md` before committing the phase close.
2. Push `snd`. The `Release` workflow (`.github/workflows/release.yml`) triggers on every push to
   `snd`, `dev`, or `main` whose changed paths are not all `**/*.md`, `docs/**`, or `LICENSE`.
   `docs/superpowers/TRACKING.md` section 3 item 2 already has every phase close end in a commit
   to `snd` followed by a coordinator push, so this step is the normal end of a phase close, not
   an extra action.
3. What the workflow does, in its own order (`.github/workflows/release.yml`):
   1. Checks out the repository at full depth (`fetch-depth: 0`), which the tag-based derivation
      needs.
   2. Fetches all tags (`git fetch --tags --force`).
   3. Sets up .NET 10.0.x.
   4. Derives the version: pipes `git tag -l` into `tools/derive-version.sh "$GITHUB_REF_NAME"`
      and writes `version`, `channel`, and `prerelease` to the step output.
   5. Runs the test suite (`dotnet test -c Release`).
   6. Publishes a self-contained `win-x64` build to `publish/win-x64`, with
      `-p:Version=<derived version>` and `-p:SourceRevisionId=${{ github.sha }}`.
   7. Installs `vpk` version 1.2.0 as a global tool on the runner, pinned to match the `Velopack`
      package version in `Directory.Packages.props`, and downloads the channel's previous
      release so the pack can emit a delta package.
   8. Writes the release notes to a file on the runner (`.github/scripts/release-notes.js`).
      The script finds the previous tag on the same channel, collects the commit messages since
      it, and asks Gemini for a short summary and one plain-language line per commit. When
      `GEMINI_API_KEY` is missing or the call fails, the file holds the raw commit list instead.
      The step never fails the release.
   9. Packs the publish output:

      ```
      vpk pack --packId GalactiLog --packVersion <version> --packDir publish/win-x64 \
        --icon src/GalactiLog.App/Assets/GalactiLog.ico \
        --mainExe GalactiLog.exe --channel <channel> --releaseNotes <the file from step 8>
      ```

      `--icon` sets the icon on Setup.exe, the shortcuts and the Add or Remove Programs
      entry. GalactiLog.exe carries its own icon from the csproj's `ApplicationIcon`.
      `--releaseNotes` puts the notes into the package, which is where the About tab reads
      them from when it offers the update.
   10. Tags the commit with the derived version and pushes the tag.
   11. Uploads to GitHub Releases:

       ```
       vpk upload github --repoUrl <this repository's URL> --token <the workflow's GitHub token> \
         --publish --releaseName <version> --tag <version> --channel <channel>
       ```

       adding `--pre` when `prerelease` is `true`.
   12. Sets the GitHub release body to the file from step 8 with `gh release edit`, so the
       release page and the About tab show the same notes.
   13. Prunes old prereleases and old stable releases (section 5 below).
4. The version to expect. Worked example, with `1.4.0` as the newest stable tag and
   `1.4.1-alpha.2` as the newest alpha tag: the push produces `1.4.1-alpha.3` on channel `alpha`.
   This repository currently carries no tags (`git tag -l` on this checkout returned nothing on
   2026-09-15), so this example is hypothetical, not a real run. It was verified against the
   landed script:

   ```
   printf '1.4.0\n1.4.1-alpha.2\n' | bash tools/derive-version.sh snd
   ```

   produced `version=1.4.1-alpha.3`, `channel=alpha`, `prerelease=true`. A reader can predict the
   number for any real tag list by running `git tag -l | bash tools/derive-version.sh snd`
   (`tools/derive-version.sh`, Task 6).
4. Where the release lands: GitHub Releases on this repository, marked prerelease, carrying the
   Velopack assets (setup executable, full package, portable zip) and the `RELEASES-alpha`
   manifest the installed application reads to find updates (`docs/packaging.md` section 4).
5. The failure case: a red suite (step 5 above) fails the job before the publish step runs, so no
   tag is pushed and no release is created (`design-spec.md` 17.5 step 5). Nothing needs cleaning
   up.
6. **A `paths-ignore` push produces no release.** A push to `snd` that touches only Markdown
   files, anything under `docs/`, or `LICENSE`, triggers neither `Release` nor `build-test.yml`.
   This is the most common "why did nothing happen" question after a documentation-only push.

## 3. Promoting to `dev`, and the rc line

**Precondition, checked once.** `dev` and `main` must exist on `origin` before this section or
section 4 can be followed:

```
git ls-remote --heads origin
```

expects `refs/heads/dev` and `refs/heads/main` listed alongside `refs/heads/snd`. As of
2026-09-15 this repository's remote carries `refs/heads/snd` only.

If either branch is missing, the repository owner creates both once, not the release operator:
`design-spec.md` 17.3 states the three branches were "created locally", so this is a one-time
setup action, not a step in an ordinary release. Create both from the Phase 0 commit `main` was
cut at (`docs/superpowers/TRACKING.md` section 6, phase table row 0: `6d44b9a`, "on main; dev and
snd branched from it"), reproducing the branch point `design-spec.md` 17.3 describes:

```
git fetch origin
git push origin 6d44b9a:refs/heads/main
git push origin 6d44b9a:refs/heads/dev
```

Then mark `branch-merge-policy.yml`'s `check-source-branch` check required in branch protection
for both `dev` and `main`, at `https://github.com/chvvkumar/GalactiLog-Windows/settings/branches`
or with `gh api repos/chvvkumar/GalactiLog-Windows/branches/<branch>/protection`, so the
merge-direction rule in step 2 below is enforced rather than only reported.

Run `git fetch origin` before every checkout of `dev` or `main` in this document, so a local
clone that has not yet seen a newly created branch resolves it.

1. Open a pull request from `snd` into `dev`. Any other source branch fails
   `branch-merge-policy.yml`, which compares `github.head_ref` against the literal string `snd`
   for a pull request targeting `dev` (`.github/workflows/branch-merge-policy.yml`).
2. That workflow only reports a failing status check; it does not block the merge by itself.
   Blocking the merge requires marking the check required in branch protection for `dev`, which
   the workflow cannot do (`design-spec.md` 17.5). Whether that check is currently marked
   required in this repository's branch protection settings is unverified from this environment:
   no authenticated `gh` session was available when this document was written. Check it at
   `https://github.com/chvvkumar/GalactiLog-Windows/settings/branches`, or run
   `gh api repos/chvvkumar/GalactiLog-Windows/branches/dev/protection` and look for
   `check-source-branch` under `required_status_checks`.
3. The merge produces an `rc` version on the same base, with its own counter starting at 1.
   Worked example, with `1.4.0` as the newest stable tag: `dev` produces `1.4.1-rc.1` regardless
   of how many alpha tags exist on that base. Verified against the landed script:

   ```
   printf '1.4.0\n1.4.1-alpha.5\n' | bash tools/derive-version.sh dev
   ```

   produced `version=1.4.1-rc.1`, `channel=rc`, `prerelease=true`, unaffected by the alpha tag
   present in the list (`AlphaTags_DoNotAffectTheRcCounter`, `tests/GalactiLog.Core.Tests/Architecture/VersionDerivationTests.cs`).
   This example is hypothetical for the same reason as section 2: no tags exist in this
   repository today.

## 4. Promoting to `main`, and the stable release

This section shares section 3's precondition: `dev` and `main` exist on `origin`, checked with
`git ls-remote --heads origin` and created once by the repository owner if missing (see the
precondition at the start of section 3).

1. Open a pull request from `dev` into `main`. Any other source branch fails
   `branch-merge-policy.yml`, which requires the head branch to be exactly `dev` for a pull
   request targeting `main`.
2. The merge produces `X.Y.Z` with no prerelease segment, on channel `stable`, uploaded without
   `--pre`, so it is marked not a prerelease.
3. Because `main` derives from the same base `dev` has been building against
   (`design-spec.md` 17.4), a release candidate graduates to stable under the same number with the
   prerelease segment dropped: `1.4.1-rc.N` graduates to stable `1.4.1`. Verified against the
   landed script:

   ```
   printf '1.4.0\n' | bash tools/derive-version.sh main
   ```

   produced `version=1.4.1`, `channel=stable`, `prerelease=false`. The same tag list plus an `rc`
   tag on that base (`1.4.1-rc.3`) produces the identical `1.4.1`, confirming the rc tags do not
   change the stable derivation. Both are hypothetical for the same reason as sections 2 and 3.
4. Moving the major or the minor version requires a manually created tag, because the derivation
   in `tools/derive-version.sh` increments only the patch segment. This is the only manual
   tagging step in this process. On `main`, after the promotion PR from step 1 has merged:

   ```
   git fetch origin
   git checkout main
   git pull
   git tag 2.0.0
   git push origin 2.0.0
   ```

   Pushing a tag alone does not trigger `Release`, because its trigger is a branch push, not a
   tag push. Push a change to `main` next (for example, a merge from the following `dev`
   promotion) so the workflow runs again and its derivation picks up `2.0.0` as the new newest
   stable tag, producing `2.0.1` on the next release.

## 5. What the retention numbers keep

`design-spec.md` 17.5 step 13, stated as an outcome:

| Channel | Kept | Deleted |
| --- | --- | --- |
| `alpha` | the newest 2 prereleases | every older alpha release and its tag (`--cleanup-tag`) |
| `rc` | the newest 2 prereleases | every older rc release and its tag |
| `stable` | the newest 5 releases | every older stable release and its tag |

These numbers are the `prune_prerelease alpha 2` and `prune_prerelease rc 2` calls and the
`tail -n +6` selection for stable in the "Prune old prereleases" step of
`.github/workflows/release.yml`, asserted to match by
`Release_PruneKeepsTwoAlphaTwoRcAndFiveStable`
(`tests/GalactiLog.Core.Tests/Architecture/WorkflowFileTests.cs`).

Three consequences:

- **The tag is deleted with the release, not only the release.** A deleted alpha tag can change a
  later derivation, because the prerelease counter is `highest existing + 1`: deleting the highest
  alpha tag would let the next alpha reuse its number. In practice the pruner keeps the newest
  two releases on each prerelease channel, so the highest tag on that channel is always kept. This
  is why the retention number cannot safely be dropped to 1: keeping only the newest release would
  delete the tag the next derivation depends on.
- **An installed alpha whose release has been pruned loses its delta-update source.** The
  installed application's `UpdateManager` reads the channel manifest to find available releases
  (`design-spec.md` 17.1). The application still runs. It cannot delta-update from that specific
  version, because the delta source package and its asset URLs are gone with the deleted release,
  and instead takes a full update to the newest remaining release on its channel.
- **The prune runs on every release, on every branch.** A stable release on `main` also prunes old
  alpha and rc releases, because the "Prune old prereleases" step is unconditional.

## 6. Verifying an installed build's channel

The reader has an installed application and wants to know which line it is on.

1. Open Settings, About. It shows the version, the git SHA, and the release channel
   (`design-spec.md` 12.7).
2. The Diagnostics page's Versions group shows the same three values: version and git SHA are
   spec 12.8's Versions row, and the group adds a release channel row
   (`src/GalactiLog.App/ViewModels/Diagnostics/DiagnosticsViewModel.cs`, "Update channel"). The
   Update channel row shows the channel the build was installed from, and `local` on a build the
   updater did not install.
3. The diagnostics bundle's `app` object carries `version`, `git_sha`, and `channel`
   (`design-spec.md` 16.3). This is what to ask a user for in a support request: it is one file
   rather than three screenshots.
4. Without launching the application: the first line of the newest
   `%LOCALAPPDATA%\GalactiLog\logs\galactilog-*.log` names the version and the app data root.
   `AppHost` writes this line unconditionally on every startup
   (`GalactiLog {Version} starting; app data root {AppDataRoot}; cliMode {CliMode}`,
   `src/GalactiLog.App/AppHost.cs`, FIXER LIST F11).
5. The rule the channel guarantees: an installed build checks its own channel only, so a `stable`
   install never offers itself a prerelease (`design-spec.md` 17.1). It follows that moving a user
   from `alpha` to `stable` is a reinstall, not an update: there is no update path between
   channels, only within one.

## 6a. Verifying the tray, close/minimize to tray, and startup behaviour

The reader has an installed application (Phase 11, `design-spec.md` 12.11) and wants to confirm
the tray and startup behaviour works on this machine, not only in the test suite.

These bars cannot be observed by the test suite, because the headless harness leaves
`ApplicationLifetime` null (`TRACKING.md` item 29) and `BuildInfo.IsInstalled` is false everywhere
in the suite. Run every step below on the installed build, on the user's machine or a fresh
profile, never against this machine's real data folders.

1. **The tray icon.** After the install, the notification area shows one icon carrying the program
   icon. Its tooltip reads `GalactiLog` while the application is idle.
2. **The menu.** Right-click the icon: four items in order, Open, Scan now, Check for updates,
   a separator, then Exit. Open shows the window; Scan now starts a scan the status bar reflects;
   Check for updates behaves as the About tab's button does, including staying disabled with a
   reason on a build the updater did not install; Exit ends the process.
3. **Close to tray**, on by default. With the setting on, the title-bar close hides the window and
   the process stays in Task Manager; the General tab's close-to-tray control carries the sentence
   explaining where the window went. With the setting off, the close ends the process.
4. **Minimize to tray.** With the setting on, minimizing the window hides it. No taskbar button may
   remain. Open from the tray and confirm the window returns at its previous size and is not
   maximized.
5. **Start with Windows.** Turning it on creates
   `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\GalactiLog.lnk`. Read the link back
   with:

   ```
   powershell -c "$s=New-Object -ComObject WScript.Shell; $l=$s.CreateShortcut('<path to the .lnk>'); $l.TargetPath; $l.Arguments"
   ```

   Target must be `%LOCALAPPDATA%\GalactiLog\GalactiLog.exe`, the install-root stub, never a path
   under `current\`. Arguments must be `--minimized`. Turning the setting off removes the link.
   With the shortcut present, confirm Diagnostics' Paths group reads `present` and not `unknown`:
   the shortcut query runs on a thread-pool thread against an apartment-threaded COM type, and a
   marshalling failure there is reported as `unknown` even though the shortcut exists. Removing
   the shortcut removes any Startup link to the same executable, including one made by hand:
   Velopack's `DeleteShortcuts` matches by executable name and location, not by which link created
   it (`design-spec.md` 2.1.1).
6. **Start minimized, and the tray icon as the only way in.** Launch the installed exe with
   `--minimized`, with `setup_complete` false: no window and no wizard appear at boot, and the
   tray icon appears with its tooltip reading `GalactiLog` and, during a scan, the scan state, with
   the four menu items and the separator in spec order. Open from the tray: the wizard now appears,
   on this first Open. A scheduled scan completes while hidden and the log records it. Setting
   `general.start_minimized` on and launching with no arguments does the same. Diagnostics' Paths
   group reports Started minimized as `yes` for either start and `no` for an ordinary one. Because
   the tray icon is the only route to the window and to Exit while no window exists, also confirm
   the escape hatch: if the icon were ever unavailable, launching a second instance with no
   `--minimized` argument brings a window up (design-spec.md 12.11 behaviour 4).
7. **Scan completion notice.** With `general.notify_on_scan_complete` on and the window hidden, let
   a scheduled scan finish. The tray tooltip carries the run's outcome and its non-zero counts, and
   the tooltip is cleared the moment the next scan starts.
8. **Exit from the tray while a scan runs.** No confirmation is shown. The drain cancels the scan
   and waits inside its five second budget, the process ends, and the next start finds no `running`
   row left open in `scan_runs`.
9. **Second launch.** With the application running, launching the exe again brings the first
   window to the front and starts no second process; the data root holds one database. Diff the
   whole temp app data folder around the second launch, not only the database and the log
   directory, because `VelopackApp.Build().Run()` runs before the single-instance gate claims
   anything. Then launch a third time with `--minimized` while the first is still running: nothing
   appears, no window moves, and the new process's exit code is 0.
10. **Update apply.** After confirming an update from the About tab, verify the restarted instance
    actually opens: it must not be refused as a second instance by its own predecessor's mutex,
    since the update restart bypasses the shutdown drain (`design-spec.md` 17.1) and the mutex is
    released only when the old process's handles close. Also confirm no scan was in flight when
    the old process ended.
11. **Uninstall.** Observe, do not assume, whether the Startup shortcut survives. Turn Start with
    Windows on, confirm the `.lnk` exists, then uninstall the application and check whether
    `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\GalactiLog.lnk` is still there. The
    Startup folder sits outside `%LOCALAPPDATA%\GalactiLog`, which is the only directory a Velopack
    uninstall is known to remove (section 7 below), so the honest expectation is that the link
    survives and now points at a deleted stub. Record what is actually observed here rather than
    the expectation. If the link survives, tell the operator to remove it by hand at the path
    above, and flag it as an `ESCALATION`: a stale link pointing at a deleted stub is a boot-time
    error for a user who uninstalled.
12. The channel check in section 6 above is unchanged by any of this.

## 7. Rollback

Two different situations. They are not the same procedure.

### A release that should not have shipped, and no user has it yet

1. Delete the GitHub release and its tag:

   ```
   gh release delete <tag> --yes --cleanup-tag
   ```

2. Confirm the channel manifest no longer lists the deleted version. Download it from the newest
   remaining release on that channel:

   ```
   gh release download <tag> --repo chvvkumar/GalactiLog-Windows --pattern 'RELEASES-<channel>'
   ```

   where `<tag>` is that release's tag and `<channel>` is `alpha`, `rc`, or `stable`. This writes
   `RELEASES-<channel>` into the current directory; open it and confirm the deleted version's
   line is gone. Deleting a release does not rewrite a manifest already attached to an earlier
   release, so a stale entry can persist until the channel's next upload. If the deleted version
   is still listed, push one more change on that branch so the next `vpk upload github`
   regenerates and re-uploads the channel manifest.
3. The next push derives the same version number again, because the tag that the derivation
   depended on is gone. This is the reason to delete the tag together with the release.

### A release users already have, and a fix is needed

1. Do **not** delete it. Deleting a release a user has installed breaks their update path: the
   installed application's `UpdateManager` reads the channel manifest to enumerate available
   releases (`design-spec.md` 17.1), and deleting a release removes the delta source package and
   the asset URLs that manifest entry points at.
2. Push the fix and let the ordinary derivation produce the next version (section 2 or 4 above).
   Velopack updates forward only by default, so a higher version is the whole rollback mechanism.
3. To move users **back** to an earlier build, the earlier version must be re-released with a
   higher version number than what is currently out. Velopack's `UpdateOptions.AllowVersionDowngrade`
   exists, but nothing in this application's update flow calls for setting it
   (`design-spec.md` 17.1, the update flow, names no downgrade path).
   `src/GalactiLog.App/Services/VelopackUpdateChecker.cs` is the one file that constructs
   `UpdateManager`, and it passes `options: null`, so no `UpdateOptions` is set at all. A
   downgrade is not available in the field through the update mechanism, and this procedure does
   not offer it as an option.
4. **What an uninstall does to the catalogue.** From the version that moved the data root, the
   database, the settings, the logs and the thumbnail cache sit at
   `%LOCALAPPDATA%\GalactiLogData`, or wherever `%APPDATA%\GalactiLog\datapath.json` names, and a
   Velopack uninstall removes only `%LOCALAPPDATA%\GalactiLog` (`design-spec.md` 17.2, check 15 of
   `docs/packaging.md` section 5). The catalogue survives an uninstall, so backing it up first is
   advice rather than a required step.

   A build installed **before** that version still keeps its data at `%LOCALAPPDATA%\GalactiLog`,
   and an uninstall of one takes the catalogue with it: observed 2026-09-15 on 1.0.0-alpha.1, the
   directory was gone after the uninstall along with the Start menu entry and the HKCU uninstall
   entry. Before uninstalling such a build, close the application and copy all three of
   `galactilog.db`, `galactilog.db-wal` and `galactilog.db-shm` out of that directory together.
   The database file carries the settings document as well as the catalogue, and the write-ahead
   log can still hold most of the content after a clean exit: on the 2026-09-15 build the main
   file was 4 KB beside a 3.3 MB `-wal`, so copying `galactilog.db` alone copies an almost empty
   library. Restore the same three files together.
5. The local escape hatch for one machine: uninstall the application, run the wanted version's
   setup executable downloaded from its GitHub release, and launch it. The catalogue at
   `%LOCALAPPDATA%\GalactiLogData` is untouched and the new install reads the same pointer. When
   the machine ran a build from before the relocation, copy the three backed-up files
   (`galactilog.db`, `galactilog.db-wal` and `galactilog.db-shm`) into
   `%LOCALAPPDATA%\GalactiLogData` before launching.

## 8. What this process deliberately does not do

- No code signing. `design-spec.md` 17 does not require it, and it would add a required secret.
- No GitHub generated notes. The release name is the tag, which is what
  `vpk upload github --releaseName` sets. The body is the file the workflow wrote from the
  commit messages (section 2 step 3 item 8); `--generate-notes` belongs to `gh release create`,
  which this process does not run (`design-spec.md` 17.5).
- No macOS or Linux build (`design-spec.md` 19.2).
- No `gh release create` anywhere. `vpk upload github` is the only release-creating command in
  `.github/workflows/release.yml`, because it uploads the Velopack assets and the `RELEASES`
  manifest the installed application reads; a release created by `gh` would carry no update
  payload (`design-spec.md` 17.5).
