using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TapRunner.App.Models;

namespace TapRunner.App
{
    public partial class SettingsWindow : Window
    {
        readonly AppConfig workingConfig;
        readonly ObservableCollection<DefaultParameterRow> defaultParameterRows = new();
        readonly List<ColorRowControls> colorRows = new();

        /// <summary>The updated config, valid after ShowDialog() returns true.</summary>
        public AppConfig Result { get; private set; }

        public SettingsWindow(AppConfig source)
        {
            InitializeComponent();

            workingConfig = Clone(source);

            TapExecutablePathText.Text = workingConfig.TapExecutablePath;
            WorkingDirectoryText.Text = workingConfig.WorkingDirectory;
            StationNameText.Text = workingConfig.StationName;

            foreach (var kvp in workingConfig.DefaultParameterValues)
                defaultParameterRows.Add(new DefaultParameterRow { Name = kvp.Key, Value = kvp.Value });
            DefaultParametersGrid.ItemsSource = defaultParameterRows;

            BuildColorRows();
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

        void OkButton_Click(object sender, RoutedEventArgs e)
        {
            workingConfig.TapExecutablePath = TapExecutablePathText.Text.Trim();
            workingConfig.WorkingDirectory = WorkingDirectoryText.Text.Trim();
            workingConfig.StationName = StationNameText.Text.Trim();

            workingConfig.DefaultParameterValues = defaultParameterRows
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.Name)
                .ToDictionary(g => g.Key, g => g.Last().Value ?? "");

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
                StationName = source.StationName,
                OperatorName = source.OperatorName,
                UseTui = source.UseTui,
                DefaultParameterValues = new Dictionary<string, string>(source.DefaultParameterValues),
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

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
