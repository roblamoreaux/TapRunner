using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using TapRunner.App.Models;
using TapRunner.Protocol;

namespace TapRunner.App.Services
{
    /// <summary>Reads external parameters from a TapPlan and launches "tap.exe" to run/edit it.</summary>
    public class TapPlanService
    {
        /// <summary>
        /// Reads the external (test plan) parameters and their default values, in alphabetical
        /// order, by parsing the TapPlan's XML directly rather than shelling out to "tap.exe" -
        /// a TapPlan file already stores each external parameter's current value as
        /// Parameter="&lt;name&gt;" on the step property it belongs to (that's exactly what
        /// "tap run --list-external-parameters" itself reads). Parsing the file directly is
        /// near-instant and, unlike running the plan file through tap.exe, never touches any
        /// instruments/DUTs the plan's steps reference just to open it.
        /// </summary>
        public Task<List<ParameterEntry>> ReadExternalParametersAsync(string planPath)
            => Task.Run(() => ReadExternalParametersFromXml(planPath));

        internal static List<ParameterEntry> ReadExternalParametersFromXml(string planPath)
        {
            var doc = XDocument.Load(planPath, LoadOptions.None);
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ParameterEntry>();

            foreach (var element in doc.Descendants())
            {
                var name = (string)element.Attribute("Parameter");
                // A parameter merged across multiple steps carries the same name on each of their
                // elements; only the first (they're supposed to share one value) is kept.
                if (string.IsNullOrEmpty(name) || !seenNames.Add(name))
                    continue;

                result.Add(new ParameterEntry(name, element.Value));
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// Launches "tap.exe" to run (or, with the TUI, view/run) the given plan with the TAPRUNNER_PIPE
        /// environment variable set so the TapRunner.StatusListener plugin (if enabled in Results settings)
        /// can report status back on <paramref name="pipeName"/>. Returns the started process.
        /// </summary>
        public Process StartRun(AppConfig config, string planPath, IEnumerable<ParameterEntry> parameters, bool useTui, string pipeName)
        {
            var psi = BuildBaseStartInfo(config);
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            psi.CreateNoWindow = false;
            psi.Environment[StatusProtocol.PipeNameEnvironmentVariable] = pipeName;

            if (useTui)
            {
                // The OpenTAP "tui" CLI action does not support external-parameter overrides (-e);
                // it opens an interactive console UI where the operator sets/reviews parameters.
                psi.ArgumentList.Add("tui");
                psi.ArgumentList.Add(planPath);
            }
            else
            {
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add(planPath);
                psi.ArgumentList.Add("--non-interactive");
                foreach (var p in parameters)
                {
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add($"{p.FullName}={p.Value}");
                }
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            return process;
        }

        ProcessStartInfo BuildBaseStartInfo(AppConfig config)
        {
            var psi = new ProcessStartInfo
            {
                FileName = config.TapExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory))
                psi.WorkingDirectory = config.WorkingDirectory;

            return psi;
        }
    }
}
