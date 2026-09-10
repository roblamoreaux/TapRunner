# TapRunner

A small Windows desktop front-end for running OpenTAP TapPlans, plus an OpenTAP `ResultListener`
plugin that reports live run status back to it.

Built and verified against the OpenTAP install on this machine: **`C:\OpenTAP`, OpenTAP 9.33.0**.

## Solution layout

```
TapRunner.sln
Directory.Build.props          # pins the plugin's OpenTAP package version to 9.33.0
src/
  TapRunner.App/                # WPF app (net8.0-windows) - the operator-facing dialog
  TapRunner.StatusListener/     # OpenTAP ResultListener plugin (net9.0) - builds a .TapPackage
  TapRunner.Protocol/           # tiny shared library: the named-pipe wire protocol used by both
```

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

Requires the .NET 8+ SDK (already installed: `dotnet 10.0.400`).

```
dotnet build TapRunner.sln -c Release
```

Or open `TapRunner.sln` in Visual Studio and Build Solution. This produces:

- `src\TapRunner.App\bin\Release\net8.0-windows\TapRunner.exe` - the app.
- `src\TapRunner.StatusListener\bin\Release\TapRunner.StatusListener.1.0.0.TapPackage` - the plugin package.

## One-time setup (per machine)

1. **Install the plugin package** into your OpenTAP installation:
   ```
   "C:\OpenTAP\tap.exe" package install "<repo>\src\TapRunner.StatusListener\bin\Release\TapRunner.StatusListener.1.0.0.TapPackage"
   ```
2. **Enable it as a Result Listener**: open the OpenTAP Editor (`tap.exe editor`) or TUI, go to
   *Settings → Results*, add **"TapRunner Status"** to the configured result listeners, and make
   sure it's enabled (alongside whatever other listeners you already use, e.g. SQLite/CSV - they
   all run together; TapRunner does not disable your other listeners).
3. In `TapRunner.exe`, click **Settings...** and set the `tap.exe` path (defaults to
   `C:\OpenTAP\tap.exe`) and the working directory you want the OpenTAP process run in.

If step 2 is skipped, TapRunner still runs the plan - it just won't get any live status messages,
and the app tells you so and returns the circles to Ready rather than guessing a verdict.

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
- **No status received when a run ends** (plugin not enabled, or TUI closed without running
  anything): rather than guess Pass/Fail, the app just returns to Ready and shows a message
  explaining why - so a missing status listener can never look like a false Pass or Fail.
- Config file lives under `%ProgramData%` (shared by all operators on this station), not next to
  the .exe or under a per-user profile - easy to change if you'd rather have it per-user.

## Not yet done

- No installer/publish profile - this is source only, built with `dotnet build`/Visual Studio.
- The plugin package is not installed into `C:\OpenTAP` automatically - that's a deliberate
  choice (installing into your live OpenTAP setup should be your call), see "One-time setup" above.
