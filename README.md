# TapRunner

A small Windows desktop front-end for running OpenTAP TapPlans, plus an OpenTAP `ResultListener`
plugin that reports live run status back to it.

Built and verified against the OpenTAP install on this machine: **`C:\OpenTAP`, OpenTAP 9.33.0**.

## Solution layout

```
TapRunner.sln
Directory.Build.props          # pins the plugin's OpenTAP package version to 9.33.0
src/
  TapRunner.App/                # WPF app (net9.0-windows) - the operator-facing dialog
  TapRunner.StatusListener/     # OpenTAP ResultListener plugin (net9.0) - builds a .TapPackage
  TapRunner.Protocol/           # tiny shared library: the named-pipe wire protocol used by both
  TapRunner.Installer/          # WiX v5 project - builds TapRunnerSetup.msi
```

`TapRunner.App` targets net9.0-windows (not net8) specifically so it needs no .NET runtime beyond
what OpenTAP 9.33's own WPF-based tools (Editor, WPF Controls, Results Viewer) already require on
this machine - installing TapRunner shouldn't mean installing a second, different .NET Desktop
Runtime alongside the one OpenTAP already needs.

## How it fits together

1. `TapRunner.App` reads a TapPlan's external (test plan) parameters and their default values by
   parsing the `.TapPlan` XML directly (every external parameter is stored right on the file as
   `Parameter="<name>"` on the property it belongs to - the same thing `tap.exe run
   --list-external-parameters` itself reads) and displays them, sorted alphabetically by name, in
   a dialog for the operator to review/edit. This is near-instant and - unlike running the plan
   through `tap.exe` just to open it - never touches any instruments/DUTs the plan's steps
   reference.
2. When **Run** is pressed, it launches `tap.exe run <plan> -e Name=Value ... --non-interactive`
   (or `tap.exe tui <plan>` if "Run with TUI" is checked), passing a unique named pipe name to the
   child process via the `TAPRUNNER_PIPE` environment variable.
3. If the `TapRunner.StatusListener` plugin is installed **and enabled** in the OpenTAP bench's
   Results settings, it connects to that pipe from inside the `tap.exe` process and reports:
   `Running` → a step `Fail`/`Error` (circle goes half-lit, "pending") → the final verdict
   (`Passed`/`Failed`/`Error`/`Aborted`, circle goes fully lit). The app uses these messages to
   drive the six status circles in real time. It also reports the instant each step *starts*
   (before its verdict/outputs are known), which the app shows as "Running: &lt;step name&gt;..."
   in the status line - so a step that never publishes a result (many simple/flow-control steps
   don't) is still visibly executing, not just silent until (or unless) it finishes.
4. A runtime clock under the logo (`hh:mm:ss`, counting actual hours past 24 rather than wrapping)
   starts the instant the `tap.exe` process is spawned and stops - freezing on the final value -
   the instant the run ends, however it ends (normal finish, Abort, or an unreported process exit),
   so the operator can see how long the current or most recent run has taken.
5. **Stop** sends a "please stop" message back down the same pipe; the plugin calls
   `TestPlanRun.MainThread.Abort()`, which is OpenTAP's normal cooperative abort (finishes the
   current step, runs cleanup/post-plan-run steps, then ends - verdict is normally `Aborted`).
   **Abort** bypasses the plugin entirely and kills the `tap.exe` process tree immediately.
6. Every status-line message and every completed step is also appended to a running session log
   (**Open Log**), and any step that finishes Fail/Error additionally gets a fuller, multi-line
   entry in a second, uncapped log (**Failed**) - see "Logging" below.

## Building

Requires the .NET 8+ SDK (already installed: `dotnet 10.0.400`). The installer project needs the
WiX v5 build tooling, which restores from NuGet the first time you build it (same as the `OpenTAP`
NuGet package used by the plugin) - nothing to install by hand.

```
dotnet build TapRunner.sln -c Release
```

Or open `TapRunner.sln` in Visual Studio (with the [WiX Toolset Visual Studio
extension](https://marketplace.visualstudio.com/items?itemName=WixToolset.WixToolsetVisualStudioExtension2022)
installed, so it recognizes the `.wixproj`) and Build Solution. This produces:

- `src\TapRunner.App\bin\Release\net9.0-windows\TapRunner.exe` - the app, if you just want to run it as-is.
- `src\TapRunner.StatusListener\bin\Release\TapRunner.StatusListener.1.2.0.TapPackage` - the plugin package.
- `src\TapRunner.Installer\bin\x64\Release\TapRunnerSetup.msi` - the installer (see below).

## Installing with TapRunnerSetup.msi

This is a **per-user install - no admin rights or UAC prompt needed**. Double-click
`TapRunnerSetup.msi` (or `msiexec /i TapRunnerSetup.msi` for a silent `/quiet` install, still with
no elevation prompt). It:

1. Installs the app to `%LocalAppData%\Programs\TapRunner` (the same convention VS Code's own
   per-user installer uses) for the current Windows user, and adds a Start Menu shortcut for them.
   A different operator signed in on the same station would need to run the installer themselves too.
2. Drops the plugin's `.TapPackage` under `%LocalAppData%\Programs\TapRunner\Plugin` for reference/manual reinstall.
3. Best-effort runs `tap.exe package install ...--overwrite` on that file, using whatever `tap.exe`
   is first on `PATH` (which is how the OpenTAP installer itself makes `tap` runnable from anywhere).
   If OpenTAP isn't installed, or `tap` isn't on `PATH`, this step silently does nothing - it never
   fails or blocks the TapRunner install itself, since machine setups vary.

Uninstalling TapRunner (Windows Settings → Apps, or the same .msi again) removes the app and Start
Menu shortcut, but does **not** uninstall the plugin package from OpenTAP - a live, working
OpenTAP install isn't something an app uninstaller should touch. Remove it yourself with
`tap package uninstall "TapRunner.StatusListener"` if you ever need to.

Windows Installer's classic validation rules (the "ICE" checks the linker runs) assume any file
installed under a per-user folder should be tracked via its own HKCU registry key rather than the
file itself, so it can be individually "repaired" if deleted - doing that for every one of the
app's ~10 harvested build output files would mean hand-writing a component per file instead of one
harvested file glob, just to satisfy an advisory best-practice check that has no bearing on the
package actually installing/uninstalling correctly. `TapRunner.Installer.wixproj` suppresses those
specific checks (`ICE38`, `ICE64`, `ICE91`) rather than doing that.

**Either way**, one manual step remains after installing (can't be automated - it's a one-time
choice in the OpenTAP bench settings, not a file the installer can just drop in place):

- **Enable "TapRunner Status" as a Result Listener**: open the OpenTAP Editor (`tap.exe editor`) or
  TUI, go to *Settings → Results*, add **"TapRunner Status"** to the configured result listeners
  (it was installed in the step above), and make sure it's enabled - alongside whatever other
  listeners you already use (SQLite/CSV etc.); they all run together, TapRunner doesn't disable them.
- In `TapRunner.exe`, click **Settings...** and confirm the `tap.exe` path (defaults to
  `C:\OpenTAP\tap.exe`) and the working directory you want the OpenTAP process run in.

If the Result Listener step is skipped, TapRunner still runs the plan - it just won't get any live
status messages, and the app tells you so and lights the Error circle rather than guessing a verdict.

### Why WiX v5, not the current v7

The installer project pins `WixToolset.Sdk/5.0.2` (and matching v5.0.2 extensions) rather than the
latest WiX Toolset (v7). Starting with v6, the `wix` CLI requires accepting a new "Open Source
Maintenance Fee" EULA before it will do anything - that's a licensing decision for New Eagle to
make deliberately, not one to accept on your behalf while building this. WiX v5 remains under the
original open license with no such prompt, and has everything this installer needs.

## Configuration file

`%ProgramData%\TapRunner\config.json` (machine-wide; created on first run). Fields:

- `LastTapPlanPath` - auto-updated; reopened and its parameters reloaded on startup if the file still exists.
- `WorkingDirectory`, `TapExecutablePath` - where/what to run.
- `OpenTapProfile` - which OpenTAP bench settings profile to run with, edited via **Settings →
  OpenTAP profile**. The dropdown lists every subdirectory of `<TapExecutablePath's folder>\Settings\Bench`
  (each one is a profile OpenTAP itself created) - it's also a plain editable text box, so a
  profile name can be typed in directly if it's not in the list (or doesn't exist yet). Blank (the
  default) means don't pass `--settings` at all, so OpenTAP uses its own default ("Default"). Shown
  in the main window next to Station as "Profile: &lt;name&gt;" when set. **Only applies to a
  headless run** - like external parameters, the `tui` CLI action has no `--settings` option, so
  TUI mode always uses whatever profile OpenTAP itself currently has selected.
- `StationName`, `OperatorName` - free text; OperatorName is also editable directly in the main window.
  StationName defaults to the Windows device name the first time TapRunner runs on a machine (i.e.
  there's no `config.json` yet - typically right after installing); after that, whatever's saved is
  what loads, so clearing it stays cleared rather than being silently re-filled.
- `LogFilePath` - where the session log is written, edited via **Settings → Log file**. Defaults to
  `%ProgramData%\TapRunner\TapRunner.log.txt`. See "Logging" below.
- `DefaultParameterValues` - name → value overrides, edited via **Settings → Default parameter values**.
  A blank/missing entry falls back to the TapPlan's own default. These are only changed
  deliberately in Settings - a value typed into the dialog for one run is never silently saved
  as the new default. Use the same short name shown in the main window (e.g. `PowerOn`), not
  OpenTAP's full internal name (`Parameters \ PowerOn`) - see below. They're applied automatically
  right after a plan loads, and any time **Load Defaults** (top-right of the parameters list) is
  pressed - e.g. after changing them in Settings without reopening the plan. Only parameters that
  actually have a saved default are touched; anything typed into the parameter list afterward is
  never overwritten again on its own. Going the other direction, **Settings → Copy From Current
  Plan** takes a snapshot of every parameter (and its current value) shown in the main window right
  now and copies it into this list - a quick way to capture values you've dialed in for one plan
  as the new saved defaults, without retyping them. It only stages the change in the dialog; click
  **OK** to actually save it.
- `StatusColors` - per-status `{ Color, Flash }` (hex color + whether it flashes when fully lit),
  editable under **Settings → Status Colors**. Defaults: Ready = blue, Running = flashing green,
  Passed = green, Failed = orange, Error = red, Aborted = dark red.

## Logging

- **Open Log** opens `LogFilePath` in whatever your machine has associated with `.txt` files. Every
  status-line message (Running/Stop requested/Aborted/Run finished/etc.) and every completed step
  gets one timestamped line, so it's a full running transcript of a session - it just keeps
  growing; nothing is trimmed or rotated.
- **Failed** opens a separate, scrollable TapRunner window (not an external editor) showing the
  failure log - a second file saved next to the main log as `<name>.Failures.txt`, containing only
  the steps that finished with a `Fail` or `Error` verdict. Each entry is a multi-line block
  (timestamp, verdict, step name, then every published result on its own line) rather than the
  single compact line shown in the app's own step history. The window opens scrolled to the most
  recent failure; **Refresh** re-reads the file for a run still in progress.
- Each run's first failure (if any) is preceded by a header block with the TapPlan's file name and
  when the run started, so it's always clear which run a given block of failures belongs to - this
  matters most when the failure log is kept across runs (see below), but is written the same way
  either way.
- By default the failure log just keeps accumulating across runs, never capped or trimmed.
  **Settings → "Clear the failure log at the start of each run"** switches that: check it and the
  file is wiped at the start of every run, so it only ever holds the current run's failures. The
  main session log is unaffected either way - it always just keeps growing.
- Both logs are best-effort: a failure to write to either (locked file, missing permissions, full
  disk) is silently ignored rather than interrupting the actual test run.

## Assumptions / known limitations (please confirm these are what you want)

- **Serial number field**: any parameter whose (displayed, see below) name contains "serial"
  (case-insensitive) gets keyboard focus - with its current value fully selected - when a plan
  loads, so the operator can scan/type over it immediately. If a default is saved for it under
  Settings, it's prefilled like any other parameter (fully selected, so a scan still overwrites it
  in one motion); otherwise it comes from the TapPlan itself, same as everything else.
- **Redundant "Parameters \ " prefix stripped from display only.** Every external parameter in
  your plans lives under one "Parameters" group, so that prefix is shown nowhere in the UI or in
  Settings - just the meaningful part (e.g. `PowerOn`, or `Input \ Value` for a nested one). OpenTAP
  still needs the real, full name to run the plan (`-e Parameters \ PowerOn=0`), so that full name
  is kept internally and used automatically - you never have to type it. If the same external
  parameter name is ever attached to more than one step (a merged parameter), only the first one
  found in the file is used.
- **Parameter editors are plain text boxes.** Reading parameters straight from the plan XML (your
  original choice, vs. embedding OpenTap.dll) gives names and default values as text but not their
  real type, so there's no dropdown for enum/`AvailableValues` parameters or a checkbox for
  booleans - just a text box, same as you'd type after `-e Name=`.
- **TUI mode has no parameter or profile pass-through.** OpenTAP's `tap.exe tui` CLI action doesn't
  accept `-e` overrides or `--settings`, so neither the parameter values entered in TapRunner's
  dialog nor the selected OpenTAP profile are carried into a TUI session - the operator sets/
  reviews parameters inside the TUI itself, and it runs with whatever profile OpenTAP already has
  selected there. Live status circles still work there.
- **Pending vs. final Fail/Error**: the spec called out the Failed circle going half-lit on a step
  failure mid-run; I applied the same "pending" treatment to the Error circle for a step `Error`
  verdict, for symmetry. Only the circle matching the plan's *final* verdict goes fully lit.
- **Failure log detail comes from the same compact summary shown in the app**, just reformatted -
  one result per line instead of comma-joined - rather than a richer wire message from the plugin.
  It's not literally everything OpenTAP knows about the step, but it's already as complete as what
  TapRunner receives over the pipe.
- **No status received when a run ends** (plugin not enabled/connected, a licensing error, a
  crash, or - with TUI - the operator closing it without running anything): the app never guesses
  Pass/Fail, but it does light the **Error** circle (and always turns Running off) with a message
  explaining why, rather than quietly returning to Ready - a real failure should never look like
  nothing happened.
- Config file lives under `%ProgramData%` (shared by all operators on this station), not next to
  the .exe or under a per-user profile - easy to change if you'd rather have it per-user.

## Not yet done

- The installer is unsigned - Windows SmartScreen/UAC will show an "Unknown publisher" warning.
  Code-signing would need a New Eagle code-signing certificate, which isn't something to set up
  without asking.
- No auto-update - each machine needs `TapRunnerSetup.msi` re-run to pick up a new version (the
  `MajorUpgrade` element in `Package.wxs` handles clean upgrades/downgrade-blocking when you do).
- Enabling "TapRunner Status" under Settings → Results in OpenTAP is still a manual, one-time step
  per machine (see above) - it's a bench-settings choice, not a file the installer can drop in place.
