using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TapRunner.App.Services
{
    /// <summary>
    /// Pulls the Error-level lines out of the OpenTAP session log that one tap.exe process wrote.
    ///
    /// Some Errors never belong to a step: a licensing check that fails, or a resource that can't
    /// be opened, ends the plan before any step starts - so there is no step verdict to report and
    /// the only record of why is OpenTAP's own log. This reads it so the reason can go in
    /// TapRunner's failure log instead of just "Error".
    /// </summary>
    public static class OpenTapLogReader
    {
        // tap.exe names its log "tap <start time>.txt", the Editor "SessionLog <start time>.txt".
        static readonly Regex LogNameRegex = new(
            @"^(?:SessionLog|tap) (?<ts>\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})\.txt$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The last few distinct Error-level messages in the session log of the tap.exe process that
        /// started at <paramref name="processStart"/> (found in "&lt;tap.exe folder&gt;\SessionLogs" by the start
        /// time in its file name, so a second OpenTAP instance - e.g. the Editor - isn't mistaken
        /// for it). Empty if the log can't be found or read; never throws.
        /// </summary>
        public static List<string> GetRunErrors(string tapExePath, DateTime processStart, int max = 5)
        {
            var errors = new List<string>();
            try
            {
                var tapDir = Path.GetDirectoryName(tapExePath);
                if (string.IsNullOrEmpty(tapDir))
                    return errors;

                var logDir = Path.Combine(tapDir, "SessionLogs");
                if (!Directory.Exists(logDir))
                    return errors;

                string best = null;
                var bestDelta = TimeSpan.MaxValue;
                foreach (var file in Directory.EnumerateFiles(logDir, "*.txt"))
                {
                    var match = LogNameRegex.Match(Path.GetFileName(file));
                    if (!match.Success ||
                        !DateTime.TryParseExact(match.Groups["ts"].Value, "yyyy-MM-dd HH-mm-ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
                        continue;

                    var delta = (started - processStart).Duration();
                    if (delta <= TimeSpan.FromSeconds(10) && delta < bestDelta)
                    {
                        best = file;
                        bestDelta = delta;
                    }
                }

                if (best == null)
                    return errors;

                // The process may still be writing to it - open it so that's allowed.
                using var stream = new FileStream(best, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // "<time> ; <source> ; <level> ; <message>"
                    var parts = line.Split(new[] { ';' }, 4);
                    if (parts.Length < 4 || parts[2].Trim() != "Error")
                        continue;

                    var message = parts[3].Trim();
                    if (message.Length == 0)
                        continue;
                    if (message.Length > 300)
                        message = message.Substring(0, 300) + "...";
                    if (!errors.Contains(message))
                        errors.Add(message);
                }
            }
            catch
            {
                // Best-effort - a log we can't read just means a less specific failure entry.
            }

            return errors.Count > max ? errors.GetRange(errors.Count - max, max) : errors;
        }
    }
}
