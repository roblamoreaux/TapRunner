using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TapRunner.App.Models;
using TapRunner.Protocol;

namespace TapRunner.App.Services
{
    /// <summary>Reads external parameters from a TapPlan and launches "tap.exe" to run/edit it.</summary>
    public class TapPlanService
    {
        // Matches lines like:  "  Parameters \ Count = 4"   (as printed by "tap run --list-external-parameters")
        static readonly Regex ParameterLineRegex = new(@"^\s*Parameters \\ (?<name>.+?) = (?<value>.*)$", RegexOptions.Compiled);

        /// <summary>
        /// Reads the external (test plan) parameters and their default values by shelling out to
        /// "tap.exe run &lt;plan&gt; --list-external-parameters". Throws on failure to start the process;
        /// returns an empty list if the plan has no external parameters.
        /// </summary>
        public async Task<List<ParameterEntry>> ReadExternalParametersAsync(AppConfig config, string planPath)
        {
            var psi = BuildBaseStartInfo(config);
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(planPath);
            psi.ArgumentList.Add("--list-external-parameters");
            psi.ArgumentList.Add("--non-interactive");

            var output = await RunAndCaptureAsync(psi).ConfigureAwait(false);
            return ParseExternalParameters(output);
        }

        internal static List<ParameterEntry> ParseExternalParameters(string output)
        {
            var result = new List<ParameterEntry>();
            using var reader = new StringReader(output);
            string line;
            bool inList = false;
            while ((line = reader.ReadLine()) != null)
            {
                var match = ParameterLineRegex.Match(line);
                if (match.Success)
                {
                    inList = true;
                    result.Add(new ParameterEntry(match.Groups["name"].Value, match.Groups["value"].Value));
                }
                else if (inList)
                {
                    // First non-matching line after the parameter block ends the list.
                    break;
                }
            }
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
                    psi.ArgumentList.Add($"{p.Name}={p.Value}");
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

        async Task<string> RunAndCaptureAsync(ProcessStartInfo psi)
        {
            using var process = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            var sync = new object();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sync) sb.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sync) sb.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync().ConfigureAwait(false);

            lock (sync)
                return sb.ToString();
        }
    }
}
