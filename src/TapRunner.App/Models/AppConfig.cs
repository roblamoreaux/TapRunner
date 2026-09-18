using System;
using System.Collections.Generic;
using System.IO;

namespace TapRunner.App.Models
{
    /// <summary>
    /// Persisted TapRunner configuration (%ProgramData%\TapRunner\config.json).
    /// Any blank/missing entry in <see cref="DefaultParameterValues"/> falls back to the
    /// default value read from the TapPlan itself.
    /// </summary>
    public class AppConfig
    {
        /// <summary>Full path to the last TapPlan file opened. Prefilled/reopened on startup.</summary>
        public string LastTapPlanPath { get; set; } = "";

        /// <summary>
        /// Path to TapRunner's own running session log (.txt) - opened via the "Open Log" button
        /// with whatever the operator's machine uses to open .txt files. Editable under Settings.
        /// The failed/error step log lives alongside it (see <see cref="LogService"/>).
        /// </summary>
        public string LogFilePath { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TapRunner", "TapRunner.log.txt");

        /// <summary>
        /// If true, the failed/error step log is wiped at the start of every run, so it only ever
        /// holds the current run's failures. If false (the default), it just keeps accumulating
        /// across runs - each run's failures are headed with the plan name and start time so they
        /// stay distinguishable. Editable under Settings.
        /// </summary>
        public bool ClearFailureLogOnRun { get; set; } = false;

        /// <summary>Directory to run the OpenTAP program in (its working directory).</summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>Path to the OpenTAP CLI executable (tap.exe) to run.</summary>
        public string TapExecutablePath { get; set; } = @"C:\OpenTAP\tap.exe";

        /// <summary>
        /// Name of the OpenTAP bench settings profile to run with - a subdirectory of
        /// &lt;OpenTAP install dir&gt;\Settings\Bench, passed as "tap run --settings &lt;name&gt;".
        /// Blank means don't pass --settings at all, so OpenTAP uses its own default ("Default").
        /// Only applies to a headless run - the "tui" CLI action has no --settings option.
        /// </summary>
        public string OpenTapProfile { get; set; } = "";

        /// <summary>
        /// Test station name. Defaults to the Windows device name the first time TapRunner ever
        /// runs on a machine (i.e. there's no config.json yet - right after installing, typically);
        /// once saved, that value (blank or not) is what's loaded from then on, so a deliberately
        /// cleared station name is never silently re-filled.
        /// </summary>
        public string StationName { get; set; } = Environment.MachineName;

        /// <summary>Operator name.</summary>
        public string OperatorName { get; set; } = "";

        /// <summary>Whether to launch with the TUI (text UI) instead of headless, last time it was used.</summary>
        public bool UseTui { get; set; }

        /// <summary>
        /// Operator-chosen default values for external test plan parameters, keyed by parameter name.
        /// A blank/missing value means "use the TapPlan's own default". Applies to every parameter,
        /// serial number fields included - a saved value here always wins over the TapPlan's own.
        /// </summary>
        public Dictionary<string, string> DefaultParameterValues { get; set; } = new();

        public StatusColorsConfig StatusColors { get; set; } = new();
    }
}
