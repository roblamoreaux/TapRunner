using System;
using System.IO;
using System.Text;

namespace TapRunner.App.Services
{
    /// <summary>
    /// Writes TapRunner's own running session log (every status change and step, one line each,
    /// timestamped), plus a second, uncapped log of just the steps that finished with a Fail or
    /// Error verdict - with fuller, multi-line detail per entry than the compact one-liners shown
    /// in the app itself. Both are plain .txt files so they open in whatever the operator's
    /// machine uses for text files.
    /// </summary>
    public class LogService
    {
        public string LogFilePath { get; private set; }

        /// <summary>The failed/error step log lives next to the main log, named "&lt;name&gt;.Failures.txt".</summary>
        public string FailureLogFilePath => GetFailureLogPath(LogFilePath);

        string currentPlanName = "";
        DateTime currentRunStartTime;
        bool failureHeaderWritten = true; // nothing to head until a run actually starts

        public LogService(string logFilePath)
        {
            SetLogFilePath(logFilePath);
        }

        /// <summary>
        /// Call once when a run starts, so the failure log's next entry (if any) is headed with the
        /// plan name and start time. If <paramref name="clearFailureLog"/> is true, the failure log
        /// is wiped first (Settings > "Clear the failure log at the start of each run"); otherwise
        /// it just keeps accumulating across runs, as it always has.
        /// </summary>
        public void BeginRun(string planName, bool clearFailureLog)
        {
            currentPlanName = string.IsNullOrWhiteSpace(planName) ? "(unknown plan)" : planName;
            currentRunStartTime = DateTime.Now;
            failureHeaderWritten = false;

            if (clearFailureLog)
            {
                try
                {
                    EnsureDirectory(FailureLogFilePath);
                    File.WriteAllText(FailureLogFilePath, "");
                }
                catch
                {
                    // Best-effort, same as every other log write.
                }
            }
        }

        public void SetLogFilePath(string path)
        {
            LogFilePath = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(Path.GetTempPath(), "TapRunner.log.txt")
                : path;
        }

        public static string GetFailureLogPath(string logFilePath)
        {
            var dir = Path.GetDirectoryName(logFilePath);
            var name = Path.GetFileNameWithoutExtension(logFilePath);
            var ext = Path.GetExtension(logFilePath);
            if (string.IsNullOrEmpty(ext))
                ext = ".txt";
            var failureFileName = $"{name}.Failures{ext}";
            return string.IsNullOrEmpty(dir) ? failureFileName : Path.Combine(dir, failureFileName);
        }

        /// <summary>Creates the log file (and its failure log) if they don't exist yet, so there's
        /// always something to open from the main window's log buttons.</summary>
        public void EnsureLogFilesExist()
        {
            TryEnsureFile(LogFilePath);
            TryEnsureFile(FailureLogFilePath);
        }

        /// <summary>Appends one timestamped line to the general session log.</summary>
        public void AppendLine(string message)
        {
            try
            {
                EnsureDirectory(LogFilePath);
                File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never crash or block the app - a locked/missing log file isn't fatal.
            }
        }

        /// <summary>
        /// Appends a detailed, multi-line block to the failure log for one step that finished with
        /// a Fail or Error verdict. Never trimmed/capped - unlike the in-app step history, this
        /// file just keeps growing for as long as the log file itself exists.
        /// </summary>
        public void AppendFailure(string stepName, string verdict, string outputs, string errorText = "")
        {
            try
            {
                var path = FailureLogFilePath;
                EnsureDirectory(path);

                var sb = new StringBuilder();

                // Head this run's first failure with the plan name and when it started, so a
                // persistent log spanning multiple runs still makes clear which run each block
                // of failures below belongs to.
                if (!failureHeaderWritten)
                {
                    sb.Append('#', 70).AppendLine();
                    sb.AppendLine($"Test Plan: {currentPlanName}");
                    sb.AppendLine($"Run started: {currentRunStartTime:yyyy-MM-dd HH:mm:ss}");
                    sb.Append('#', 70).AppendLine();
                    sb.AppendLine();
                    failureHeaderWritten = true;
                }

                sb.Append('=', 70).AppendLine();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {verdict.ToUpperInvariant()}  {stepName}");

                bool hasError = !string.IsNullOrWhiteSpace(errorText);
                bool hasOutputs = !string.IsNullOrWhiteSpace(outputs);
                if (hasError || hasOutputs)
                    sb.Append('-', 70).AppendLine();

                if (hasError)
                {
                    // One "Error:" line per line of text, so a list of messages stays readable.
                    foreach (var errorLine in errorText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        sb.AppendLine($"Error: {errorLine.Trim()}");
                }

                if (hasOutputs)
                {
                    // Break the compact "Table: col=v, col=v; Table2: col=v" summary out onto its
                    // own line per result, instead of cramming it all onto a single line.
                    foreach (var group in outputs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    foreach (var item in group.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var line = item.Trim();
                        if (line.Length > 0)
                            sb.AppendLine(line);
                    }
                }

                sb.AppendLine();
                File.AppendAllText(path, sb.ToString());
            }
            catch
            {
                // As above - a logging failure must never affect the actual test run.
            }
        }

        static void TryEnsureFile(string path)
        {
            try
            {
                EnsureDirectory(path);
                if (!File.Exists(path))
                    File.WriteAllText(path, "");
            }
            catch
            {
                // Best-effort only.
            }
        }

        static void EnsureDirectory(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
