using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TapRunner.App
{
    /// <summary>Read-only, scrollable viewer for the failed/error step log.</summary>
    public partial class FailedStepsWindow : Window
    {
        readonly string filePath;

        public FailedStepsWindow(string filePath)
        {
            InitializeComponent();
            this.filePath = filePath;
            LoadContent();
        }

        void LoadContent()
        {
            try
            {
                ContentText.Text = File.Exists(filePath)
                    ? ReadLog()
                    : "No steps have failed or errored yet.";
            }
            catch (Exception ex)
            {
                ContentText.Text = $"Could not read the failure log:\n\n{ex.Message}";
            }

            // Land on the most recent failure - still fully scrollable back through the rest.
            ContentText.CaretIndex = ContentText.Text.Length;
            ContentText.ScrollToEnd();
        }

        /// <summary>Reads the log while still letting TapRunner's log writer append to it.</summary>
        string ReadLog()
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show(this, "No steps have failed or errored yet - there is nothing to save.",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Save the failure log",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"TapRunner Failures {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                // The file as it is now (it may have grown since this window last refreshed).
                File.WriteAllText(dialog.FileName, ReadLog());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save the failure log:\n{dialog.FileName}\n\n{ex.Message}",
                    "TapRunner", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadContent();

        void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
