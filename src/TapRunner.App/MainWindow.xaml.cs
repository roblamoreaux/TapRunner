using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TapRunner.App.Models;
using TapRunner.App.Services;
using TapRunner.Protocol;

namespace TapRunner.App
{
    public partial class MainWindow : Window
    {
        readonly ConfigService configService = new();
        readonly TapPlanService tapPlanService = new();
        AppConfig config;
        LogService logService;

        readonly ObservableCollection<ParameterEntry> parameters = new();

        Process runningProcess;
        StatusPipeServer pipeServer;
        bool receivedTerminalStatus;
        bool userInitiatedAbort;
        bool errorStepLoggedThisRun;
        DateTime runProcessStart;

        // Oldest first, newest last; capped at MaxStepHistoryLines so older ones roll off the top.
        readonly List<string> stepHistory = new();
        const int MaxStepHistoryLines = 1000;

        // Runtime display under the logo: starts the instant the process is spawned, stops (and
        // freezes on the final value) the instant the run ends, however it ends.
        readonly Stopwatch runStopwatch = new();
        readonly DispatcherTimer runtimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        public MainWindow()
        {
            InitializeComponent();

            runtimeTimer.Tick += (_, _) => UpdateRuntimeDisplay();

            config = configService.Load();
            ParametersList.ItemsSource = parameters;

            MoveTapRunnerLogOutOfOpenTapLogs();

            logService = new LogService(config.LogFilePath);
            logService.EnsureLogFilesExist();

            OperatorNameText.Text = config.OperatorName;
            StationNameText.Text = string.IsNullOrWhiteSpace(config.StationName) ? "" : $"Station: {config.StationName}";
            ProfileNameText.Text = string.IsNullOrWhiteSpace(config.OpenTapProfile) ? "" : $"Profile: {config.OpenTapProfile}";
            UseTuiCheckBox.IsChecked = config.UseTui;

            ApplyStatusColorsFromConfig();
            SetIdleState(clearVerdicts: true);

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        /// <summary>
        /// Older configs pointed the TapRunner log at a file in OpenTAP's SessionLogs folder (e.g.
        /// Latest.txt, OpenTAP's live log), mixing the two. Move TapRunner's log back to its own
        /// default location, and keep that old file as the OpenTAP log the main window opens.
        /// </summary>
        void MoveTapRunnerLogOutOfOpenTapLogs()
        {
            try
            {
                var openTapLog = config.ResolveOpenTapLogPath();
                var logDir = Path.GetDirectoryName(config.LogFilePath ?? "");
                var openTapDir = Path.GetDirectoryName(openTapLog);
                if (string.IsNullOrEmpty(logDir) || string.IsNullOrEmpty(openTapDir) ||
                    !string.Equals(Path.GetFullPath(logDir), Path.GetFullPath(openTapDir), StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.IsNullOrWhiteSpace(config.OpenTapLogPath))
                    config.OpenTapLogPath = config.LogFilePath;
                config.LogFilePath = new AppConfig().LogFilePath;
                configService.Save(config);
            }
            catch
            {
                // Best-effort - worst case the old setting stays as it was.
            }
        }

        async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Prefill/open the last TapPlan used, if it still exists. Otherwise the Browse button
            // (Open Plan File) refreshes everything when a file is picked.
            if (!string.IsNullOrWhiteSpace(config.LastTapPlanPath) && File.Exists(config.LastTapPlanPath))
            {
                await LoadPlanAsync(config.LastTapPlanPath);
            }
        }

        void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (IsRunning())
            {
                var result = MessageBox.Show(this, "A test is currently running. Abort it and exit?", "TapRunner",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                KillRunningProcess();
            }

            SaveCurrentSettingsToConfig();
            configService.Save(config);
        }

        bool IsRunning() => runningProcess != null && !runningProcess.HasExited;

        void ApplyStatusColorsFromConfig()
        {
            Apply(ReadyCircle, config.StatusColors.Ready);
            Apply(RunningCircle, config.StatusColors.Running);
            Apply(PassedCircle, config.StatusColors.Passed);
            Apply(FailedCircle, config.StatusColors.Failed);
            Apply(ErrorCircle, config.StatusColors.Error);
            Apply(AbortedCircle, config.StatusColors.Aborted);

            static void Apply(Controls.StatusCircle circle, StatusColorConfig cfg)
            {
                try { circle.OnColor = (Color)ColorConverter.ConvertFromString(cfg.Color); }
                catch { circle.OnColor = Colors.Gray; }
                circle.Flash = cfg.Flash;
            }
        }

        void SetIdleState(bool clearVerdicts)
        {
            ReadyCircle.State = CircleState.Full;
            RunningCircle.State = CircleState.Off;
            if (clearVerdicts)
            {
                PassedCircle.State = CircleState.Off;
                FailedCircle.State = CircleState.Off;
                ErrorCircle.State = CircleState.Off;
                AbortedCircle.State = CircleState.Off;
            }

            RunButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
            LoadDefaultsButton.IsEnabled = true;
            PowerOnButton.IsEnabled = true;
            PowerOffButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            AbortButton.IsEnabled = false;
        }

        void SetRunningState()
        {
            ReadyCircle.State = CircleState.Off;
            RunningCircle.State = CircleState.Full;
            PassedCircle.State = CircleState.Off;
            FailedCircle.State = CircleState.Off;
            ErrorCircle.State = CircleState.Off;
            AbortedCircle.State = CircleState.Off;

            RunButton.IsEnabled = false;
            BrowseButton.IsEnabled = false;
            SettingsButton.IsEnabled = false;
            LoadDefaultsButton.IsEnabled = false;
            PowerOnButton.IsEnabled = false;
            PowerOffButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            AbortButton.IsEnabled = true;
        }

        async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select a TapPlan file",
                Filter = "OpenTAP Plan files (*.TapPlan)|*.TapPlan|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (!string.IsNullOrWhiteSpace(config.LastTapPlanPath) && File.Exists(config.LastTapPlanPath))
                dialog.InitialDirectory = Path.GetDirectoryName(config.LastTapPlanPath);

            if (dialog.ShowDialog(this) == true)
            {
                await LoadPlanAsync(dialog.FileName);
            }
        }

        async Task LoadPlanAsync(string path)
        {
            RunButton.IsEnabled = false;
            BrowseButton.IsEnabled = false;
            SetStatusMessage($"Reading parameters from \"{Path.GetFileName(path)}\"...");

            PlanPathText.Text = path;
            parameters.Clear();

            try
            {
                var planParameters = await tapPlanService.ReadExternalParametersAsync(path);

                foreach (var p in planParameters)
                {
                    // Every parameter - serial number included - falls back to the operator's saved
                    // default (Settings > Default parameter values) if one is set, or the TapPlan's
                    // own default otherwise. This happens right after the plan loads, same as
                    // clicking "Load Defaults" would do.
                    p.Value = TryGetSavedDefault(p, out var savedDefault) ? savedDefault : p.PlanDefaultValue;
                    ApplyHiddenSetting(p);
                    parameters.Add(p);
                }

                config.LastTapPlanPath = path;
                configService.Save(config);

                SetStatusMessage(parameters.Count > 0
                    ? $"Loaded {parameters.Count} parameter(s)."
                    : "Loaded. This plan has no external (test plan) parameters.");
            }
            catch (Exception ex)
            {
                SetStatusMessage($"Failed to read parameters: {ex.Message}");
                MessageBox.Show(this, $"Could not read external parameters from this plan:\n\n{ex.Message}",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RunButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Looks up the operator's saved default (Settings > Default parameter values) for one
        /// parameter - including a serial number field, if one was set there. Settings always wins
        /// over the TapPlan's own default when both exist.
        /// </summary>
        bool TryGetSavedDefault(ParameterEntry p, out string value)
        {
            value = null;
            if (config.DefaultParameterValues.TryGetValue(p.Name, out var saved) && !string.IsNullOrEmpty(saved))
            {
                value = saved;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Hides (or, if Settings says so, greys out) a parameter that's marked Hidden under
        /// Settings > Default parameter values. This is display-only: a hidden parameter stays in
        /// the list and is still passed to tap.exe on Run with its saved default (or the plan's).
        /// </summary>
        void ApplyHiddenSetting(ParameterEntry p)
        {
            bool hidden = config.HiddenParameters.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
            p.ApplyHiddenSetting(hidden, config.ShowHiddenParametersGreyedOut);
        }

        void ApplyHiddenSettingToAll()
        {
            foreach (var p in parameters)
                ApplyHiddenSetting(p);
        }

        /// <summary>
        /// Re-applies the operator's saved default parameter values (Settings > Default parameter
        /// values) to the currently displayed parameters - e.g. after changing them in Settings
        /// without reopening the plan. Only touches parameters that actually have a saved default;
        /// anything the operator types afterward is left alone (nothing re-applies automatically).
        /// </summary>
        void LoadDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            int applied = 0;
            foreach (var p in parameters)
            {
                if (TryGetSavedDefault(p, out var saved))
                {
                    p.Value = saved;
                    applied++;
                }
            }

            SetStatusMessage(applied > 0
                ? $"Loaded {applied} saved default parameter value(s) from Settings."
                : "No saved default parameter values apply to this plan.");
        }

        void ParameterTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox { Tag: ParameterEntry { IsSerialNumber: true } } textBox)
            {
                textBox.Focus();
                textBox.SelectAll();
            }
        }

        async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PlanPathText.Text))
            {
                MessageBox.Show(this, "Select a TapPlan file first.", "TapRunner", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!CheckTapExecutable())
                return;

            await StartRunAsync(PlanPathText.Text, parameters, UseTuiCheckBox.IsChecked == true, runLabel: null);
        }

        async void PowerOnButton_Click(object sender, RoutedEventArgs e)
            => await RunPowerPlanAsync("Power On", config.PowerOnPlanPath);

        async void PowerOffButton_Click(object sender, RoutedEventArgs e)
            => await RunPowerPlanAsync("Power Off", config.PowerOffPlanPath);

        bool CheckTapExecutable()
        {
            if (File.Exists(config.TapExecutablePath))
                return true;

            MessageBox.Show(this, $"The configured OpenTAP executable was not found:\n{config.TapExecutablePath}\n\nSet it under Settings.",
                "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        /// <summary>
        /// Runs the power on / power off plan from Settings: always headless (the TUI checkbox is
        /// about the main plan), through the same status circles, timer and logging as any run.
        /// Settings' default parameter values override the plan's own values here too; whatever is
        /// typed in the main window's parameter list belongs to the main plan and is not used.
        /// </summary>
        async Task RunPowerPlanAsync(string label, string configuredPath)
        {
            if (!CheckTapExecutable())
                return;

            var planPath = ResolvePlanPath(configuredPath);
            if (planPath == null || !File.Exists(planPath))
            {
                MessageBox.Show(this, $"The {label} plan was not found:\n{planPath ?? "(not set)"}\n\nSet it under Settings.",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            List<ParameterEntry> planParameters;
            try
            {
                planParameters = await tapPlanService.ReadExternalParametersAsync(planPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not read the {label} plan:\n\n{ex.Message}", "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            foreach (var p in planParameters)
                p.Value = TryGetSavedDefault(p, out var saved) ? saved : p.PlanDefaultValue;

            await StartRunAsync(planPath, planParameters, useTui: false, runLabel: label);
        }

        /// <summary>
        /// A bare plan name like "PowerON.TapPlan" is looked for in the working directory first,
        /// then next to tap.exe; a full path is used as-is. If nothing exists yet, returns the first
        /// place it would have looked, so the "not found" message names a real location.
        /// </summary>
        string ResolvePlanPath(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured))
                return null;

            configured = configured.Trim();
            if (Path.IsPathRooted(configured))
                return configured;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
                candidates.Add(Path.Combine(config.WorkingDirectory, configured));
            var tapDir = Path.GetDirectoryName(config.TapExecutablePath);
            if (!string.IsNullOrWhiteSpace(tapDir))
                candidates.Add(Path.Combine(tapDir, configured));

            return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault() ?? configured;
        }

        /// <summary>Starts tap.exe on <paramref name="planPath"/> and puts the window into its "running" state.</summary>
        async Task StartRunAsync(string planPath, IEnumerable<ParameterEntry> runParameters, bool useTui, string runLabel)
        {
            SaveCurrentSettingsToConfig();
            configService.Save(config);

            receivedTerminalStatus = false;
            userInitiatedAbort = false;
            errorStepLoggedThisRun = false;
            logService.BeginRun(Path.GetFileName(planPath), config.ClearFailureLogOnRun);

            pipeServer = new StatusPipeServer();
            pipeServer.StatusReceived += OnStatusReceived;
            pipeServer.Start();

            try
            {
                runningProcess = tapPlanService.StartRun(config, planPath, runParameters, useTui, pipeServer.PipeName);
            }
            catch (Exception ex)
            {
                pipeServer.Dispose();
                pipeServer = null;
                MessageBox.Show(this, $"Failed to start the OpenTAP process:\n\n{ex.Message}", "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try { runProcessStart = runningProcess.StartTime; }
            catch { runProcessStart = DateTime.Now; }

            runningProcess.Exited += RunningProcess_Exited;
            SetRunningState();
            SetStatusMessage(runLabel == null ? "Running..." : $"Running {runLabel} ({Path.GetFileName(planPath)})...");
            stepHistory.Clear();
            UpdateStepHistoryDisplay();
            StartRuntimeTimer();

            // tap.exe opens its own console window (headless run or TUI), which can grab the
            // foreground as it appears. Give it a moment to actually show up, then reclaim focus
            // so the operator lands back on TapRunner's status circles rather than the console.
            await Task.Delay(400);
            BringToFront();
        }

        void BringToFront()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Show();
            Activate();

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(hwnd);
        }

        void UseTuiCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            // TUI opens the plan in OpenTAP's interactive text UI rather than running it headless,
            // so "Run" is misleading while it's checked - the operator is going there to edit/view it.
            RunButton.Content = UseTuiCheckBox.IsChecked == true ? "Edit" : "Run";
        }

        void OnStatusReceived(string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (StepStartedMessage.TryParse(message, out var started))
                {
                    // Live "what's executing right now" indicator - separate from the completed-step
                    // history below, and not logged, so quick/no-result steps are still visible as
                    // they run instead of only appearing (or not) after they finish.
                    StatusMessageText.Text = $"Running: {started.Name}...";
                    return;
                }

                if (StepStatusMessage.TryParse(message, out var step))
                {
                    // The step history display must always update, even if something below (or a
                    // future change) throws - one bad step must never stop the rest from showing.
                    try
                    {
                        var summary = $"{step.Name} — {step.Verdict}";
                        if (!string.IsNullOrWhiteSpace(step.Outputs))
                            summary += $"   {step.Outputs}";
                        if (!string.IsNullOrWhiteSpace(step.Error))
                            summary += $"   Error: {step.Error}";

                        AddStepHistory(summary);

                        logService.AppendLine($"Step: {summary}");

                        bool isError = string.Equals(step.Verdict, "Error", StringComparison.OrdinalIgnoreCase);
                        if (isError || string.Equals(step.Verdict, "Fail", StringComparison.OrdinalIgnoreCase))
                        {
                            logService.AppendFailure(step.Name, step.Verdict, step.Outputs, step.Error);
                            if (isError)
                                errorStepLoggedThisRun = true;
                        }
                    }
                    catch
                    {
                        // Never let a step update crash the pipe's message loop.
                    }
                    return;
                }

                switch (message)
                {
                    case StatusProtocol.StatusRunning:
                        RunningCircle.State = CircleState.Full;
                        break;
                    case StatusProtocol.StatusStepFailPending:
                        FailedCircle.State = CircleState.Partial;
                        break;
                    case StatusProtocol.StatusStepErrorPending:
                        ErrorCircle.State = CircleState.Partial;
                        break;
                    case StatusProtocol.StatusPassed:
                        FinishRun(PassedCircle);
                        break;
                    case StatusProtocol.StatusFailed:
                        FinishRun(FailedCircle);
                        break;
                    case StatusProtocol.StatusError:
                        FinishRun(ErrorCircle);
                        break;
                    case StatusProtocol.StatusAborted:
                        FinishRun(AbortedCircle);
                        break;
                }
            });
        }

        void AddStepHistory(string summary)
        {
            stepHistory.Add(summary);
            if (stepHistory.Count > MaxStepHistoryLines)
                stepHistory.RemoveAt(0);

            UpdateStepHistoryDisplay();
        }

        void UpdateStepHistoryDisplay()
        {
            if (stepHistory.Count == 0)
            {
                StepHistoryText.Text = "No steps run yet.";
                return;
            }

            // Only follow new lines to the bottom if the operator hasn't scrolled up to review
            // history - otherwise appending text would keep yanking them back down.
            bool wasAtBottom = StepHistoryText.VerticalOffset >= StepHistoryText.ExtentHeight - StepHistoryText.ViewportHeight - 1.0;

            StepHistoryText.Text = string.Join(Environment.NewLine, stepHistory);

            if (wasAtBottom)
                StepHistoryText.ScrollToEnd();
        }

        void StartRuntimeTimer()
        {
            runStopwatch.Restart();
            UpdateRuntimeDisplay();
            runtimeTimer.Start();
        }

        /// <summary>Idempotent - safe to call from every "the run just ended" path, however it ended.</summary>
        void StopRuntimeTimer()
        {
            if (!runtimeTimer.IsEnabled)
                return;
            runtimeTimer.Stop();
            UpdateRuntimeDisplay(); // one last update so the frozen value is exact, not off by up to a second
        }

        void UpdateRuntimeDisplay()
        {
            var elapsed = runStopwatch.Elapsed;
            RuntimeText.Text = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }

        void FinishRun(Controls.StatusCircle verdictCircle)
        {
            receivedTerminalStatus = true;
            StopRuntimeTimer();

            // An Error that no step owns - a licensing check failing, or a resource (instrument/DUT)
            // that can't be opened, ends the plan before any step starts - would otherwise leave
            // nothing at all in the failure log or the history, just a red circle. OpenTAP's own log
            // has the reason, so use that.
            string errorDetail = null;
            if (ReferenceEquals(verdictCircle, ErrorCircle) && !errorStepLoggedThisRun)
            {
                errorDetail = LogPlanLevelError("The run ended with an Error verdict, but no step reported one.");
            }

            PassedCircle.State = CircleState.Off;
            FailedCircle.State = CircleState.Off;
            ErrorCircle.State = CircleState.Off;
            AbortedCircle.State = CircleState.Off;
            verdictCircle.State = CircleState.Full;

            RunningCircle.State = CircleState.Off;
            SetStatusMessage($"Run finished: {verdictCircle.Label}." + (errorDetail != null ? $" {errorDetail}" : ""));
        }

        /// <summary>
        /// One entry in the failure log, and a line in the history box, for an Error that isn't tied
        /// to a step (see FinishRun and RunningProcess_Exited), so every Error run leaves something
        /// there. It carries whatever OpenTAP itself logged at Error level during this run - e.g. the
        /// licence message - read from that process's session log. Returns the most recent of those
        /// (shortened, for the status line), or null if none were found.
        /// </summary>
        string LogPlanLevelError(string text)
        {
            var openTapErrors = OpenTapLogReader.GetRunErrors(config.TapExecutablePath, runProcessStart);

            var lines = new List<string> { text };
            if (openTapErrors.Count > 0)
                lines.AddRange(openTapErrors.Select(e => "OpenTAP logged: " + e));
            else
                lines.Add("No Error lines were found in the OpenTAP session log for this run - check it (and the licence) for details.");

            logService.AppendFailure("(test plan)", "Error", "", string.Join("\n", lines));
            errorStepLoggedThisRun = true;

            var detail = openTapErrors.Count > 0 ? openTapErrors[openTapErrors.Count - 1] : null;
            if (detail != null && detail.Length > 200)
                detail = detail.Substring(0, 200) + "...";

            AddStepHistory($"(test plan) — Error   {detail ?? text}");
            return detail;
        }

        async void RunningProcess_Exited(object sender, EventArgs e)
        {
            // The plugin writes its last step, the final verdict and then closes the pipe just
            // before tap.exe exits (an Error aborts the plan, so all of that lands within a few
            // milliseconds of the exit). Let the pipe finish delivering it before tearing it down,
            // otherwise the last step - often the very one that errored - is dropped.
            try
            {
                var server = pipeServer;
                if (server != null)
                    await server.DrainAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Best-effort - never let it stop the cleanup below.
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (pipeServer != null)
                {
                    pipeServer.StatusReceived -= OnStatusReceived;
                    pipeServer.Dispose();
                    pipeServer = null;
                }

                // The process is no longer running, full stop - Running must never be left lit/flashing
                // past this point, regardless of what (if anything) was reported. Same for the runtime
                // timer - if FinishRun already stopped it, this is a harmless no-op.
                RunningCircle.State = CircleState.Off;
                StopRuntimeTimer();

                if (!userInitiatedAbort && !receivedTerminalStatus)
                {
                    // The process ended without ever reporting a final verdict - e.g. a licensing
                    // error, a crash, or the "TapRunner Status" listener isn't enabled/connected.
                    // Surface this as an error rather than quietly going back to Ready, so a real
                    // failure can never look like nothing happened.
                    PassedCircle.State = CircleState.Off;
                    FailedCircle.State = CircleState.Off;
                    AbortedCircle.State = CircleState.Off;
                    ErrorCircle.State = CircleState.Full;

                    var exitCode = TryGetExitCode(runningProcess);
                    var message = "Process exited without reporting a result" +
                        (exitCode is int code ? $" (exit code {code})" : "");
                    var detail = LogPlanLevelError(message);
                    SetStatusMessage(detail != null
                        ? $"{message} - OpenTAP logged: {detail}"
                        : $"{message} - check for a licensing error or other startup failure, and confirm " +
                          "\"TapRunner Status\" is enabled under Settings > Results in OpenTAP.");
                }

                ReadyCircle.State = CircleState.Full;
                RunButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
                SettingsButton.IsEnabled = true;
                LoadDefaultsButton.IsEnabled = true;
                PowerOnButton.IsEnabled = true;
                PowerOffButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                AbortButton.IsEnabled = false;

                runningProcess = null;
            });
        }

        void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (pipeServer == null)
            {
                SetStatusMessage("No status listener is connected - Stop request not delivered. Use Abort to force-stop.");
                return;
            }
            pipeServer.SendStop();
            SetStatusMessage("Stop requested - waiting for the test plan to finish the current step...");
        }

        void AbortButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsRunning())
                return;

            var result = MessageBox.Show(this, "This will immediately kill the running OpenTAP process. Continue?",
                "TapRunner", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            userInitiatedAbort = true;
            KillRunningProcess();
            StopRuntimeTimer();

            RunningCircle.State = CircleState.Off;
            PassedCircle.State = CircleState.Off;
            FailedCircle.State = CircleState.Off;
            ErrorCircle.State = CircleState.Off;
            AbortedCircle.State = CircleState.Full;
            SetStatusMessage("Aborted (process killed).");
        }

        void KillRunningProcess()
        {
            try
            {
                if (IsRunning())
                    runningProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // already exiting/exited
            }
        }

        static int? TryGetExitCode(Process process)
        {
            try { return process?.ExitCode; }
            catch { return null; }
        }

        void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

        void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentSettingsToConfig();

            var settingsWindow = new SettingsWindow(config, parameters) { Owner = this };
            if (settingsWindow.ShowDialog() == true)
            {
                config = settingsWindow.Result;
                configService.Save(config);
                StationNameText.Text = string.IsNullOrWhiteSpace(config.StationName) ? "" : $"Station: {config.StationName}";
                ProfileNameText.Text = string.IsNullOrWhiteSpace(config.OpenTapProfile) ? "" : $"Profile: {config.OpenTapProfile}";
                ApplyStatusColorsFromConfig();
                ApplyHiddenSettingToAll();
                logService.SetLogFilePath(config.LogFilePath);
                logService.EnsureLogFilesExist();
                if (!IsRunning())
                    SetIdleState(clearVerdicts: false);
            }
        }

        void OpenLogButton_Click(object sender, RoutedEventArgs e)
        {
            // OpenTAP's own log - TapRunner never writes it, so don't create it if it's missing.
            var path = config.ResolveOpenTapLogPath();
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, $"The OpenTAP log was not found:\n{path}\n\nCheck the OpenTAP log path under Settings.",
                        "TapRunner", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open the log file:\n{path}\n\n{ex.Message}",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void FailedButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new FailedStepsWindow(logService.FailureLogFilePath) { Owner = this };
            window.Show();
        }

        void SaveCurrentSettingsToConfig()
        {
            // Only operator name and the TUI checkbox are remembered automatically between runs.
            // Default parameter values are only changed deliberately, via Settings > Default Parameters -
            // a value typed for one unit's run must never silently become the new default.
            config.OperatorName = OperatorNameText.Text;
            config.UseTui = UseTuiCheckBox.IsChecked == true;
        }

        /// <summary>Sets the status line and appends the same text to TapRunner's session log.</summary>
        void SetStatusMessage(string message)
        {
            StatusMessageText.Text = message;
            logService.AppendLine(message);
        }
    }

    static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
