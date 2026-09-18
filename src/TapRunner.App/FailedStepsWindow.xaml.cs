using System;
using System.IO;
using System.Windows;

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
                    ? File.ReadAllText(filePath)
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

        void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadContent();

        void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
