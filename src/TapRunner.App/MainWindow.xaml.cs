using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        readonly ObservableCollection<ParameterEntry> parameters = new();

        Process runningProcess;
        StatusPipeServer pipeServer;
        bool receivedTerminalStatus;
        bool userInitiatedAbort;

        public MainWindow()
        {
            InitializeComponent();

            config = configService.Load();
            ParametersList.ItemsSource = parameters;

            OperatorNameText.Text = config.OperatorName;
            StationNameText.Text = string.IsNullOrWhiteSpace(config.StationName) ? "" : $"Station: {config.StationName}";
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
            StatusMessageText.Text = $"Reading parameters from \"{Path.GetFileName(path)}\"...";

            PlanPathText.Text = path;
            parameters.Clear();

            try
            {
                var planParameters = await tapPlanService.ReadExternalParametersAsync(path);

                foreach (var p in planParameters)
                {
                    // Serial number fields always start blank for the operator to fill in;
                    // everything else falls back to the operator's saved default, or the TapPlan's
                    // own default if none was saved (or it was left blank).
                    if (!p.IsSerialNumber &&
                        config.DefaultParameterValues.TryGetValue(p.Name, out var savedDefault) &&
                        !string.IsNullOrEmpty(savedDefault))
                    {
                        p.Value = savedDefault;
                    }
                    else
                    {
                        p.Value = p.PlanDefaultValue;
                    }

                    parameters.Add(p);
                }

                config.LastTapPlanPath = path;
                configService.Save(config);

                StatusMessageText.Text = parameters.Count > 0
                    ? $"Loaded {parameters.Count} parameter(s)."
                    : "Loaded. This plan has no external (test plan) parameters.";
            }
            catch (Exception ex)
            {
                StatusMessageText.Text = $"Failed to read parameters: {ex.Message}";
                MessageBox.Show(this, $"Could not read external parameters from this plan:\n\n{ex.Message}",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RunButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
            }
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
            StatusMessageText.Text = "Running...";
            LastStepText.Text = "No steps run yet.";
            LastStepText.ToolTip = null;

            await Task.CompletedTask;
        }

        void OnStatusReceived(string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (StepStatusMessage.TryParse(message, out var step))
                {
                    var text = $"Last step: {step.Name} — {step.Verdict}";
                    if (!string.IsNullOrWhiteSpace(step.Outputs))
                        text += $"   {step.Outputs}";
                    LastStepText.Text = text;
                    LastStepText.ToolTip = text;
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

        void FinishRun(Controls.StatusCircle verdictCircle)
        {
            receivedTerminalStatus = true;
            PassedCircle.State = CircleState.Off;
            FailedCircle.State = CircleState.Off;
            ErrorCircle.State = CircleState.Off;
            AbortedCircle.State = CircleState.Off;
            verdictCircle.State = CircleState.Full;

            RunningCircle.State = CircleState.Off;
            StatusMessageText.Text = $"Run finished: {verdictCircle.Label}.";
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
                // past this point, regardless of what (if anything) was reported.
                RunningCircle.State = CircleState.Off;

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
                    StatusMessageText.Text = "Process exited without reporting a result" +
                        (exitCode is int code ? $" (exit code {code})" : "") +
                        " - check for a licensing error or other startup failure, and confirm " +
                        "\"TapRunner Status\" is enabled under Settings > Results in OpenTAP.";
                }

                ReadyCircle.State = CircleState.Full;
                RunButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
                SettingsButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                AbortButton.IsEnabled = false;

                runningProcess = null;
            });
        }

        void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (pipeServer == null)
            {
                StatusMessageText.Text = "No status listener is connected - Stop request not delivered. Use Abort to force-stop.";
                return;
            }
            pipeServer.SendStop();
            StatusMessageText.Text = "Stop requested - waiting for the test plan to finish the current step...";
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

            RunningCircle.State = CircleState.Off;
            PassedCircle.State = CircleState.Off;
            FailedCircle.State = CircleState.Off;
            ErrorCircle.State = CircleState.Off;
            AbortedCircle.State = CircleState.Full;
            StatusMessageText.Text = "Aborted (process killed).";
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

            var settingsWindow = new SettingsWindow(config) { Owner = this };
            if (settingsWindow.ShowDialog() == true)
            {
                config = settingsWindow.Result;
                configService.Save(config);
                StationNameText.Text = string.IsNullOrWhiteSpace(config.StationName) ? "" : $"Station: {config.StationName}";
                ApplyStatusColorsFromConfig();
                if (!IsRunning())
                    SetIdleState(clearVerdicts: false);
            }
        }

        void SaveCurrentSettingsToConfig()
        {
            // Only operator name and the TUI checkbox are remembered automatically between runs.
            // Default parameter values are only changed deliberately, via Settings > Default Parameters -
            // a value typed for one unit's run must never silently become the new default.
            config.OperatorName = OperatorNameText.Text;
            config.UseTui = UseTuiCheckBox.IsChecked == true;
        }
    }
}
