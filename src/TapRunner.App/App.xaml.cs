using System.Windows;
using TapRunner.App.Services;

namespace TapRunner.App
{
    public partial class App : Application
    {
        /// <summary>
        /// "TapRunner.exe --set-results-viewer &lt;exe&gt;" saves that program as the View Results viewer
        /// and exits without opening a window. The installer runs it when it finds Test Run Viewer
        /// installed.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Length == 2 && e.Args[0] == "--set-results-viewer")
            {
                try
                {
                    var configService = new ConfigService();
                    var config = configService.Load();
                    config.ResultsViewerPath = e.Args[1];
                    configService.Save(config);
                    Shutdown();
                }
                catch
                {
                    Shutdown(1);
                }
                return;
            }
            new MainWindow().Show();
        }
    }
}
