using System.Collections.Generic;

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

        /// <summary>Directory to run the OpenTAP program in (its working directory).</summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>Path to the OpenTAP CLI executable (tap.exe) to run.</summary>
        public string TapExecutablePath { get; set; } = @"C:\OpenTAP\tap.exe";

        /// <summary>Test station name.</summary>
        public string StationName { get; set; } = "";

        /// <summary>Operator name.</summary>
        public string OperatorName { get; set; } = "";

        /// <summary>Whether to launch with the TUI (text UI) instead of headless, last time it was used.</summary>
        public bool UseTui { get; set; }

        /// <summary>
        /// Operator-chosen default values for external test plan parameters, keyed by parameter name.
        /// A blank/missing value means "use the TapPlan's own default". Never applied to a parameter
        /// that looks like a serial number - that field is always left blank for the operator to fill in.
        /// </summary>
        public Dictionary<string, string> DefaultParameterValues { get; set; } = new();

        public StatusColorsConfig StatusColors { get; set; } = new();
    }
}
