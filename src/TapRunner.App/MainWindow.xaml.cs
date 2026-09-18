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
            if (!File.Exists(config.TapExecutablePath))
            {
                MessageBox.Show(this, $"The configured OpenTAP executable was not found:\n{config.TapExecutablePath}\n\nSet it under Settings.",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SaveCurrentSettingsToConfig();
            configService.Save(config);

            receivedTerminalStatus = false;
            userInitiatedAbort = false;
            logService.BeginRun(Path.GetFileName(PlanPathText.Text), config.ClearFailureLogOnRun);

            pipeServer = new StatusPipeServer();
            pipeServer.StatusReceived += OnStatusReceived;
            pipeServer.Start();

            try
            {
                runningProcess = tapPlanService.StartRun(config, PlanPathText.Text, parameters, UseTuiCheckBox.IsChecked == true, pipeServer.PipeName);
            }
            catch (Exception ex)
            {
                pipeServer.Dispose();
                pipeServer = null;
                MessageBox.Show(this, $"Failed to start the OpenTAP process:\n\n{ex.Message}", "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            runningProcess.Exited += RunningProcess_Exited;
            SetRunningState();
            SetStatusMessage("Running...");
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

                        stepHistory.Add(summary);
                        if (stepHistory.Count > MaxStepHistoryLines)
                            stepHistory.RemoveAt(0);

                        UpdateStepHistoryDisplay();

                        logService.AppendLine($"Step: {summary}");
                        if (string.Equals(step.Verdict, "Fail", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(step.Verdict, "Error", StringComparison.OrdinalIgnoreCase))
                        {
                            logService.AppendFailure(step.Name, step.Verdict, step.Outputs);
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
            PassedCircle.State = CircleState.Off;
            FailedCircle.State = CircleState.Off;
            ErrorCircle.State = CircleState.Off;
            AbortedCircle.State = CircleState.Off;
            verdictCircle.State = CircleState.Full;

            RunningCircle.State = CircleState.Off;
            SetStatusMessage($"Run finished: {verdictCircle.Label}.");
        }

        void RunningProcess_Exited(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
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
                    SetStatusMessage("Process exited without reporting a result" +
                        (exitCode is int code ? $" (exit code {code})" : "") +
                        " - check for a licensing error or other startup failure, and confirm " +
                        "\"TapRunner Status\" is enabled under Settings > Results in OpenTAP.");
                }

                ReadyCircle.State = CircleState.Full;
                RunButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
                SettingsButton.IsEnabled = true;
                LoadDefaultsButton.IsEnabled = true;
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
                logService.SetLogFilePath(config.LogFilePath);
                logService.EnsureLogFilesExist();
                if (!IsRunning())
                    SetIdleState(clearVerdicts: false);
            }
        }

        void OpenLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                logService.EnsureLogFilesExist();
                Process.Start(new ProcessStartInfo(logService.LogFilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open the log file:\n{logService.LogFilePath}\n\n{ex.Message}",
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
