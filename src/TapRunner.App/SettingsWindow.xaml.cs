using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TapRunner.App.Models;
using TapRunner.App.Services;

namespace TapRunner.App
{
    public partial class SettingsWindow : Window
    {
        readonly AppConfig workingConfig;
        readonly List<ParameterEntry> currentParameters;
        readonly ObservableCollection<DefaultParameterRow> defaultParameterRows = new();
        readonly List<ColorRowControls> colorRows = new();
        string resultsXmlPath;
        string originalSqliteDatabasePath; // null if Results.xml/its SQLite listener wasn't found

        /// <summary>The updated config, valid after ShowDialog() returns true.</summary>
        public AppConfig Result { get; private set; }

        /// <param name="source">The config to edit a copy of.</param>
        /// <param name="currentParameters">
        /// The parameters currently shown in the main window (with their current values), for
        /// "Copy From Current Plan" - empty/null if no plan is loaded there.
        /// </param>
        public SettingsWindow(AppConfig source, IEnumerable<ParameterEntry> currentParameters = null)
        {
            InitializeComponent();

            workingConfig = Clone(source);
            this.currentParameters = currentParameters?.ToList() ?? new List<ParameterEntry>();

            TapExecutablePathText.Text = workingConfig.TapExecutablePath;
            PopulateProfileList();
            ProfileComboBox.Text = workingConfig.OpenTapProfile;
            LoadSqliteDatabaseSetting();
            WorkingDirectoryText.Text = workingConfig.WorkingDirectory;
            StationNameText.Text = workingConfig.StationName;
            LogFilePathText.Text = workingConfig.LogFilePath;
            OpenTapLogPathText.Text = workingConfig.OpenTapLogPath;
            ClearFailureLogOnRunCheckBox.IsChecked = workingConfig.ClearFailureLogOnRun;
            PowerOnPlanText.Text = workingConfig.PowerOnPlanPath;
            PowerOffPlanText.Text = workingConfig.PowerOffPlanPath;
            ShowHiddenGreyedOutCheckBox.IsChecked = workingConfig.ShowHiddenParametersGreyedOut;

            bool IsHiddenName(string name) =>
                workingConfig.HiddenParameters.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

            foreach (var kvp in workingConfig.DefaultParameterValues)
                defaultParameterRows.Add(new DefaultParameterRow { Name = kvp.Key, Value = kvp.Value, IsHidden = IsHiddenName(kvp.Key) });

            // A parameter can be hidden without having a default value of its own - it just keeps
            // the TapPlan's - so those names get a row too (blank value, Hidden ticked).
            foreach (var hidden in workingConfig.HiddenParameters)
            {
                if (!defaultParameterRows.Any(r => string.Equals(r.Name, hidden, StringComparison.OrdinalIgnoreCase)))
                    defaultParameterRows.Add(new DefaultParameterRow { Name = hidden, Value = "", IsHidden = true });
            }
            DefaultParametersGrid.ItemsSource = defaultParameterRows;

            BuildColorRows();
        }

        /// <summary>
        /// Shows the SQLite result listener's database from OpenTAP's Results.xml (next to the
        /// tap.exe configured when this dialog opened). Not kept in TapRunner's own config -
        /// Results.xml is the one place OpenTAP reads it from, so it's only written back there on OK.
        /// </summary>
        void LoadSqliteDatabaseSetting()
        {
            resultsXmlPath = OpenTapResultsSettings.GetResultsXmlPath(workingConfig.TapExecutablePath);
            originalSqliteDatabasePath = OpenTapResultsSettings.ReadSqliteDatabasePath(resultsXmlPath, out var problem);
            SqliteDatabaseText.Text = originalSqliteDatabasePath ?? "";

            if (problem != null)
            {
                SqliteDatabaseText.IsEnabled = false;
                BrowseSqliteDatabaseButton.IsEnabled = false;
                SqliteDatabaseNote.Text = problem;
            }
            else
            {
                SqliteDatabaseNote.Text = $"From {resultsXmlPath}. A relative name is in the tap.exe folder. " +
                    "Applies to the next run; close the OpenTAP Editor first, or it may save its own copy over it.";
            }
            SqliteDatabaseNote.Visibility = Visibility.Visible;
        }

        void BrowseSqliteDatabase_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Select the SQLite results database (an existing one, or a new name)",
                Filter = "OpenTAP results database (*.TapResults)|*.TapResults|All files (*.*)|*.*",
                OverwritePrompt = false, // results are added to an existing database, not overwritten
            };

            var current = SqliteDatabaseText.Text.Trim();
            var tapDir = System.IO.Path.GetDirectoryName(workingConfig.TapExecutablePath ?? "");
            if (string.IsNullOrEmpty(tapDir))
                tapDir = null;
            if (current.Length > 0)
            {
                var full = System.IO.Path.IsPathRooted(current) || tapDir == null ? current : System.IO.Path.Combine(tapDir, current);
                dialog.FileName = System.IO.Path.GetFileName(full);
                var dir = System.IO.Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }
            else if (tapDir != null && System.IO.Directory.Exists(tapDir))
            {
                dialog.InitialDirectory = tapDir;
            }

            if (dialog.ShowDialog(this) == true)
                SqliteDatabaseText.Text = dialog.FileName;
        }

        /// <summary>
        /// Fills the profile dropdown with the subdirectory names under
        /// "&lt;OpenTAP install dir&gt;\Settings\Bench" (each one is a bench settings profile) -
        /// derived from the current Tap executable path. Missing/inaccessible just means an empty
        /// list; the combo box is still editable, so a profile name can always be typed manually.
        /// </summary>
        void PopulateProfileList()
        {
            ProfileComboBox.Items.Clear();
            try
            {
                var tapDir = System.IO.Path.GetDirectoryName(TapExecutablePathText.Text);
                if (string.IsNullOrWhiteSpace(tapDir))
                    return;

                var benchDir = System.IO.Path.Combine(tapDir, "Settings", "Bench");
                if (!System.IO.Directory.Exists(benchDir))
                    return;

                foreach (var dir in System.IO.Directory.GetDirectories(benchDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    ProfileComboBox.Items.Add(System.IO.Path.GetFileName(dir));
            }
            catch
            {
                // Best-effort only - an empty list just means typing the profile name manually.
            }
        }

        void CopyFromCurrentPlanButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentParameters.Count == 0)
            {
                MessageBox.Show(this, "No test plan is currently loaded in the main window.",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int added = 0, updated = 0;
            foreach (var p in currentParameters)
            {
                var row = defaultParameterRows.FirstOrDefault(r => string.Equals(r.Name, p.Name, StringComparison.Ordinal));
                if (row != null)
                {
                    row.Value = p.Value;
                    updated++;
                }
                else
                {
                    defaultParameterRows.Add(new DefaultParameterRow { Name = p.Name, Value = p.Value, IsHidden = p.IsHidden });
                    added++;
                }
            }

            MessageBox.Show(this,
                $"Copied {currentParameters.Count} parameter(s) from the loaded plan ({added} added, {updated} updated).\n\nClick OK to save these as the new defaults.",
                "TapRunner", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void BuildColorRows()
        {
            AddColorRow(0, "Ready", workingConfig.StatusColors.Ready);
            AddColorRow(1, "Running", workingConfig.StatusColors.Running);
            AddColorRow(2, "Passed", workingConfig.StatusColors.Passed);
            AddColorRow(3, "Failed", workingConfig.StatusColors.Failed);
            AddColorRow(4, "Error", workingConfig.StatusColors.Error);
            AddColorRow(5, "Aborted", workingConfig.StatusColors.Aborted);
        }

        void AddColorRow(int row, string label, StatusColorConfig colorConfig)
        {
            var labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(labelText, row);
            Grid.SetColumn(labelText, 0);
            ColorsGrid.Children.Add(labelText);

            var swatch = new System.Windows.Shapes.Rectangle
            {
                Width = 18, Height = 18, Stroke = Brushes.Black, StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4)
            };
            Grid.SetRow(swatch, row);
            Grid.SetColumn(swatch, 1);
            ColorsGrid.Children.Add(swatch);

            var hexBox = new TextBox { Text = colorConfig.Color, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 4, 6, 4) };
            Grid.SetRow(hexBox, row);
            Grid.SetColumn(hexBox, 2);
            ColorsGrid.Children.Add(hexBox);

            var flashCheck = new CheckBox { Content = "Flash", IsChecked = colorConfig.Flash, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(flashCheck, row);
            Grid.SetColumn(flashCheck, 3);
            ColorsGrid.Children.Add(flashCheck);

            void UpdateSwatch()
            {
                try { swatch.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexBox.Text)); }
                catch { swatch.Fill = Brushes.Transparent; }
            }
            UpdateSwatch();
            hexBox.TextChanged += (_, _) => UpdateSwatch();

            colorRows.Add(new ColorRowControls { HexBox = hexBox, FlashCheck = flashCheck });
        }

        void BrowseTapExe_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "Select tap.exe", Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true)
                TapExecutablePathText.Text = dialog.FileName;
        }

        void BrowseWorkingDirectory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select the working directory" };
            if (dialog.ShowDialog(this) == true)
                WorkingDirectoryText.Text = dialog.FolderName;
        }

        void BrowsePowerOnPlan_Click(object sender, RoutedEventArgs e) => BrowsePlanInto(PowerOnPlanText, "Select the Power On plan");

        void BrowsePowerOffPlan_Click(object sender, RoutedEventArgs e) => BrowsePlanInto(PowerOffPlanText, "Select the Power Off plan");

        void BrowsePlanInto(TextBox target, string title)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "OpenTAP Plan files (*.TapPlan)|*.TapPlan|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            // Start in the working directory (where bare plan names are looked for), if there is one.
            var startDir = WorkingDirectoryText.Text.Trim();
            if (startDir.Length > 0 && System.IO.Directory.Exists(startDir))
                dialog.InitialDirectory = startDir;

            if (dialog.ShowDialog(this) == true)
                target.Text = dialog.FileName;
        }

        void BrowseLogFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Select the log file",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                OverwritePrompt = false,
            };
            if (!string.IsNullOrWhiteSpace(LogFilePathText.Text))
            {
                dialog.FileName = System.IO.Path.GetFileName(LogFilePathText.Text);
                var dir = System.IO.Path.GetDirectoryName(LogFilePathText.Text);
                if (!string.IsNullOrWhiteSpace(dir))
                    dialog.InitialDirectory = dir;
            }
            if (dialog.ShowDialog(this) == true)
                LogFilePathText.Text = dialog.FileName;
        }

        void BrowseOpenTapLog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select the OpenTAP log file",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            };
            var dir = System.IO.Path.GetDirectoryName(OpenTapLogPathText.Text.Trim());
            if (!string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir))
                dialog.InitialDirectory = dir;
            if (dialog.ShowDialog(this) == true)
                OpenTapLogPathText.Text = dialog.FileName;
        }

        void OpenTapRunnerLog_Click(object sender, RoutedEventArgs e)
        {
            var path = LogFilePathText.Text.Trim();
            try
            {
                var log = new LogService(path);
                log.EnsureLogFilesExist();
                Process.Start(new ProcessStartInfo(log.LogFilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open the log file:\n{path}\n\n{ex.Message}",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // OpenTAP's own file - only touched if the value was actually changed here.
            var sqlitePath = SqliteDatabaseText.Text.Trim();
            if (originalSqliteDatabasePath != null && sqlitePath.Length > 0 && sqlitePath != originalSqliteDatabasePath)
            {
                try
                {
                    OpenTapResultsSettings.WriteSqliteDatabasePath(resultsXmlPath, sqlitePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not update the SQLite database in\n{resultsXmlPath}\n\n{ex.Message}\n\n" +
                        "Nothing was changed there. Fix the problem, or set the path back, and click OK again.",
                        "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            workingConfig.TapExecutablePath = TapExecutablePathText.Text.Trim();
            workingConfig.OpenTapProfile = ProfileComboBox.Text.Trim();
            workingConfig.WorkingDirectory = WorkingDirectoryText.Text.Trim();
            workingConfig.StationName = StationNameText.Text.Trim();
            workingConfig.LogFilePath = LogFilePathText.Text.Trim();
            workingConfig.OpenTapLogPath = OpenTapLogPathText.Text.Trim();
            workingConfig.ClearFailureLogOnRun = ClearFailureLogOnRunCheckBox.IsChecked == true;
            workingConfig.PowerOnPlanPath = PowerOnPlanText.Text.Trim();
            workingConfig.PowerOffPlanPath = PowerOffPlanText.Text.Trim();
            workingConfig.ShowHiddenParametersGreyedOut = ShowHiddenGreyedOutCheckBox.IsChecked == true;

            var namedRows = defaultParameterRows
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.Name.Trim())
                .Select(g => g.Last())
                .ToList();

            workingConfig.DefaultParameterValues = namedRows.ToDictionary(r => r.Name.Trim(), r => r.Value ?? "");
            workingConfig.HiddenParameters = namedRows.Where(r => r.IsHidden).Select(r => r.Name.Trim()).ToList();

            ApplyColorRow(colorRows[0], workingConfig.StatusColors.Ready);
            ApplyColorRow(colorRows[1], workingConfig.StatusColors.Running);
            ApplyColorRow(colorRows[2], workingConfig.StatusColors.Passed);
            ApplyColorRow(colorRows[3], workingConfig.StatusColors.Failed);
            ApplyColorRow(colorRows[4], workingConfig.StatusColors.Error);
            ApplyColorRow(colorRows[5], workingConfig.StatusColors.Aborted);

            Result = workingConfig;
            DialogResult = true;
        }

        static void ApplyColorRow(ColorRowControls row, StatusColorConfig target)
        {
            var text = row.HexBox.Text.Trim();
            try
            {
                ColorConverter.ConvertFromString(text); // validate
                target.Color = text;
            }
            catch
            {
                // keep previous value if what was typed doesn't parse
            }
            target.Flash = row.FlashCheck.IsChecked == true;
        }

        void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        static AppConfig Clone(AppConfig source)
        {
            return new AppConfig
            {
                LastTapPlanPath = source.LastTapPlanPath,
                WorkingDirectory = source.WorkingDirectory,
                TapExecutablePath = source.TapExecutablePath,
                OpenTapProfile = source.OpenTapProfile,
                StationName = source.StationName,
                LogFilePath = source.LogFilePath,
                OpenTapLogPath = source.OpenTapLogPath,
                ClearFailureLogOnRun = source.ClearFailureLogOnRun,
                OperatorName = source.OperatorName,
                UseTui = source.UseTui,
                DefaultParameterValues = new Dictionary<string, string>(source.DefaultParameterValues),
                HiddenParameters = new List<string>(source.HiddenParameters),
                ShowHiddenParametersGreyedOut = source.ShowHiddenParametersGreyedOut,
                PowerOnPlanPath = source.PowerOnPlanPath,
                PowerOffPlanPath = source.PowerOffPlanPath,
                StatusColors = new StatusColorsConfig
                {
                    Ready = new StatusColorConfig { Color = source.StatusColors.Ready.Color, Flash = source.StatusColors.Ready.Flash },
                    Running = new StatusColorConfig { Color = source.StatusColors.Running.Color, Flash = source.StatusColors.Running.Flash },
                    Passed = new StatusColorConfig { Color = source.StatusColors.Passed.Color, Flash = source.StatusColors.Passed.Flash },
                    Failed = new StatusColorConfig { Color = source.StatusColors.Failed.Color, Flash = source.StatusColors.Failed.Flash },
                    Error = new StatusColorConfig { Color = source.StatusColors.Error.Color, Flash = source.StatusColors.Error.Flash },
                    Aborted = new StatusColorConfig { Color = source.StatusColors.Aborted.Color, Flash = source.StatusColors.Aborted.Flash },
                }
            };
        }

        class ColorRowControls
        {
            public TextBox HexBox { get; set; }
            public CheckBox FlashCheck { get; set; }
        }
    }

    public class DefaultParameterRow : INotifyPropertyChanged
    {
        string name = "";
        public string Name { get => name; set { name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }

        string val = "";
        public string Value { get => val; set { val = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }

        bool isHidden;
        public bool IsHidden { get => isHidden; set { isHidden = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden))); } }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
