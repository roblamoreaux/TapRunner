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

1. `TapRunner.App` shells out to `tap.exe run <plan> --list-external-parameters` to read a
   TapPlan's external (test plan) parameters and their default values, and displays them in a
   dialog for the operator to review/edit.
2. When **Run** is pressed, it launches `tap.exe run <plan> -e Name=Value ... --non-interactive`
   (or `tap.exe tui <plan>` if "Run with TUI" is checked), passing a unique named pipe name to the
   child process via the `TAPRUNNER_PIPE` environment variable.
3. If the `TapRunner.StatusListener` plugin is installed **and enabled** in the OpenTAP bench's
   Results settings, it connects to that pipe from inside the `tap.exe` process and reports:
   `Running` → a step `Fail`/`Error` (circle goes half-lit, "pending") → the final verdict
   (`Passed`/`Failed`/`Error`/`Aborted`, circle goes fully lit). The app uses these messages to
   drive the six status circles in real time.
4. **Stop** sends a "please stop" message back down the same pipe; the plugin calls
   `TestPlanRun.MainThread.Abort()`, which is OpenTAP's normal cooperative abort (finishes the
   current step, runs cleanup/post-plan-run steps, then ends - verdict is normally `Aborted`).
   **Abort** bypasses the plugin entirely and kills the `tap.exe` process tree immediately.

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
- `src\TapRunner.StatusListener\bin\Release\TapRunner.StatusListener.1.0.0.TapPackage` - the plugin package.
- `src\TapRunner.Installer\bin\x64\Release\TapRunnerSetup.msi` - the installer (see below).

## Installing with TapRunnerSetup.msi

Double-click `TapRunnerSetup.msi` (or `msiexec /i TapRunnerSetup.msi` for a silent
`/quiet` install). It:

1. Installs the app to `Program Files\TapRunner` and adds a Start Menu shortcut.
2. Drops the plugin's `.TapPackage` under `Program Files\TapRunner\Plugin` for reference/manual reinstall.
3. Best-effort runs `tap.exe package install ...--overwrite` on that file, using whatever `tap.exe`
   is first on `PATH` (which is how the OpenTAP installer itself makes `tap` runnable from anywhere).
   If OpenTAP isn't installed, or `tap` isn't on `PATH`, this step silently does nothing - it never
   fails or blocks the TapRunner install itself, since machine setups vary.

Uninstalling TapRunner (Windows Settings → Apps, or the same .msi again) removes the app and Start
Menu shortcut, but does **not** uninstall the plugin package from OpenTAP - a live, working
OpenTAP install isn't something an app uninstaller should touch. Remove it yourself with
`tap package uninstall "TapRunner.StatusListener"` if you ever need to.

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
- `StationName`, `OperatorName` - free text; OperatorName is also editable directly in the main window.
- `DefaultParameterValues` - name → value overrides, edited via **Settings → Default parameter values**.
  A blank/missing entry falls back to the TapPlan's own default. These are only changed
  deliberately in Settings - a value typed into the dialog for one run is never silently saved
  as the new default.
- `StatusColors` - per-status `{ Color, Flash }` (hex color + whether it flashes when fully lit),
  editable under **Settings → Status Colors**. Defaults: Ready = blue, Running = flashing green,
  Passed = green, Failed = orange, Error = red, Aborted = dark red.

## Assumptions / known limitations (please confirm these are what you want)

- **Serial number field**: any parameter whose name contains "serial" (case-insensitive) is
  treated as the serial number - it's always left blank (never prefilled from saved defaults) and
  gets keyboard focus when a plan loads, so the operator can scan/type it immediately.
- **Parameter editors are plain text boxes.** Reading parameters via
  `--list-external-parameters` (your choice, vs. embedding OpenTap.dll) gives names and default
  values as text but not their real type, so there's no dropdown for enum/`AvailableValues`
  parameters or a checkbox for booleans - just a text box, same as you'd type after `-e Name=`.
- **TUI mode has no parameter pass-through.** OpenTAP's `tap.exe tui` CLI action doesn't accept
  `-e` overrides, so values entered in TapRunner's dialog are *not* carried into a TUI session -
  the operator sets/reviews them inside the TUI itself. Live status circles still work there.
- **Pending vs. final Fail/Error**: the spec called out the Failed circle going half-lit on a step
  failure mid-run; I applied the same "pending" treatment to the Error circle for a step `Error`
  verdict, for symmetry. Only the circle matching the plan's *final* verdict goes fully lit.
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
