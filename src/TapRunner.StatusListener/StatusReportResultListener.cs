using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using OpenTap;
using TapRunner.Protocol;

namespace TapRunner.StatusListener
{
    /// <summary>
    /// OpenTAP ResultListener that reports live test plan run status to the TapRunner desktop app
    /// over a named pipe, so the app can drive its Ready/Running/Passed/Failed/Error/Aborted status
    /// circles while "tap.exe" is running the plan out-of-process.
    ///
    /// This listener only does anything when it is launched (directly or indirectly) by TapRunner.App,
    /// which passes the pipe name via the TAPRUNNER_PIPE environment variable. When run normally
    /// (e.g. from the OpenTAP Editor or another script), it is a no-op.
    ///
    /// One-time setup: this listener must be added and enabled under Settings -> Results in the
    /// OpenTAP bench settings for it to run alongside your other result listeners. See the
    /// TapRunner README for details.
    /// </summary>
    [Display("TapRunner Status", Group: "TapRunner",
        Description: "Reports live test plan run status to the TapRunner desktop app over a named pipe.")]
    public class StatusReportResultListener : ResultListener
    {
        NamedPipeClientStream pipe;
        StreamWriter writer;
        Thread commandThread;
        readonly object sendLock = new object();
        bool sawFailedStep;
        bool sawErrorStep;

        // Per-step-run bookkeeping so the "last step executed" summary can include its published
        // outputs. Only ResultListener callbacks touch these, and OpenTAP guarantees those are
        // called sequentially (never in parallel), so no locking is needed here.
        readonly Dictionary<Guid, string> stepNames = new Dictionary<Guid, string>();
        readonly Dictionary<Guid, List<string>> stepOutputs = new Dictionary<Guid, List<string>>();

        /// <summary>Cap on the "outputs" summary sent per step, so one chatty step can't flood the pipe/UI.</summary>
        const int MaxOutputsLength = 300;

        public StatusReportResultListener()
        {
            Name = "TapRunner Status";
        }

        public override void OnTestPlanRunStart(TestPlanRun planRun)
        {
            sawFailedStep = false;
            sawErrorStep = false;
            stepNames.Clear();
            stepOutputs.Clear();

            var pipeName = Environment.GetEnvironmentVariable(StatusProtocol.PipeNameEnvironmentVariable);
            if (string.IsNullOrEmpty(pipeName))
            {
                // Not launched by TapRunner.App - nothing to report to.
                return;
            }

            try
            {
                var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(3000);
                pipe = client;
                writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };

                commandThread = new Thread(() => ListenForCommands(client, planRun))
                {
                    IsBackground = true,
                    Name = "TapRunnerStatusListener-Commands"
                };
                commandThread.Start();

                Send(StatusProtocol.StatusRunning);
            }
            catch (Exception ex)
            {
                Log.Warning("TapRunner Status: could not connect to pipe '{0}': {1}", pipeName, ex.Message);
                pipe = null;
                writer = null;
            }
        }

        void ListenForCommands(NamedPipeClientStream client, TestPlanRun planRun)
        {
            try
            {
                using (var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line == StatusProtocol.CommandStop)
                        {
                            Log.Info("TapRunner Status: Stop requested by TapRunner - aborting test plan run.");
                            planRun.MainThread.Abort();
                        }
                    }
                }
            }
            catch
            {
                // Pipe closed/broken - nothing more to listen for.
            }
        }

        public override void OnTestStepRunStart(TestStepRun stepRun)
        {
            stepNames[stepRun.Id] = stepRun.TestStepName;
            stepOutputs[stepRun.Id] = new List<string>();

            // Sent immediately, before the step's verdict/outputs exist, so the app can show it's
            // executing right now - otherwise a step that never publishes a result (many simple/
            // flow-control steps don't) shows nothing at all until (if ever) it completes.
            Send(new StepStartedMessage { Name = stepRun.TestStepName }.ToLine());
        }

        public override void OnResultPublished(Guid stepRunId, ResultTable result)
        {
            if (!stepOutputs.TryGetValue(stepRunId, out var list))
            {
                list = new List<string>();
                stepOutputs[stepRunId] = list;
            }

            var columns = result.Columns
                .Where(c => c.Data != null && c.Data.Length > 0)
                .Select(c => $"{c.Name}={FormatValue(c.Data.GetValue(c.Data.Length - 1))}")
                .ToList();

            if (columns.Count > 0)
                list.Add($"{result.Name}: {string.Join(", ", columns)}");
        }

        static string FormatValue(object value)
        {
            if (value is double d) return d.ToString("G6", CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString("G6", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        public override void OnTestStepRunCompleted(TestStepRun stepRun)
        {
            if (stepRun.Verdict == Verdict.Fail && !sawFailedStep)
            {
                sawFailedStep = true;
                Send(StatusProtocol.StatusStepFailPending);
            }
            else if (stepRun.Verdict == Verdict.Error && !sawErrorStep)
            {
                sawErrorStep = true;
                Send(StatusProtocol.StatusStepErrorPending);
            }

            stepNames.TryGetValue(stepRun.Id, out var name);
            stepOutputs.TryGetValue(stepRun.Id, out var outputsList);
            var outputs = outputsList != null ? string.Join("; ", outputsList) : "";
            if (outputs.Length > MaxOutputsLength)
                outputs = outputs.Substring(0, MaxOutputsLength) + "...";

            var stepStatus = new StepStatusMessage
            {
                Name = name ?? stepRun.TestStepName,
                Verdict = stepRun.Verdict.ToString(),
                Outputs = outputs,
            };
            Send(stepStatus.ToLine());

            stepNames.Remove(stepRun.Id);
            stepOutputs.Remove(stepRun.Id);
        }

        public override void OnTestPlanRunCompleted(TestPlanRun planRun, Stream logStream)
        {
            switch (planRun.Verdict)
            {
                case Verdict.Pass:
                    Send(StatusProtocol.StatusPassed);
                    break;
                case Verdict.Fail:
                    Send(StatusProtocol.StatusFailed);
                    break;
                case Verdict.Error:
                    Send(StatusProtocol.StatusError);
                    break;
                case Verdict.Aborted:
                    Send(StatusProtocol.StatusAborted);
                    break;
                default:
                    // NotSet / Inconclusive: surface as Error rather than silently reporting nothing,
                    // so an unexpected verdict is never mistaken for a normal Pass on the shop floor.
                    Send(StatusProtocol.StatusError);
                    break;
            }

            ClosePipe();
        }

        void Send(string message)
        {
            lock (sendLock)
            {
                try
                {
                    writer?.WriteLine(message);
                }
                catch (Exception ex)
                {
                    Log.Warning("TapRunner Status: failed to send '{0}': {1}", message, ex.Message);
                }
            }
        }

        void ClosePipe()
        {
            lock (sendLock)
            {
                try { writer?.Dispose(); } catch { /* already closed */ }
                try { pipe?.Dispose(); } catch { /* already closed */ }
                writer = null;
                pipe = null;
            }
        }

        public override void Close()
        {
            ClosePipe();
            base.Close();
        }
    }
}
