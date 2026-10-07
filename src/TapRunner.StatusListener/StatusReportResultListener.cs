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

        // Live overall status. A Fail/Error stays "active" until a parent step finishes with a
        // better verdict (it cleared it - OpenTAP won't count it in the plan's verdict either), or
        // a parent takes it over by finishing Fail/Error itself.
        readonly Dictionary<Guid, Guid> stepParents = new Dictionary<Guid, Guid>(); // running steps only
        readonly List<ActiveFailure> activeFailures = new List<ActiveFailure>();
        bool sawPass;
        bool failResolved;
        bool errorResolved;
        LiveStatusMessage lastLive;

        class ActiveFailure
        {
            public Verdict Verdict;
            public HashSet<Guid> Ancestors; // step runs this one is nested in
        }

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
            stepParents.Clear();
            activeFailures.Clear();
            sawPass = false;
            failResolved = false;
            errorResolved = false;
            lastLive = null;
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
            stepParents[stepRun.Id] = stepRun.Parent;

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
            UpdateLiveStatus(stepRun);

            stepNames.TryGetValue(stepRun.Id, out var name);
            stepOutputs.TryGetValue(stepRun.Id, out var outputsList);
            var outputs = outputsList != null ? string.Join("; ", outputsList) : "";
            if (outputs.Length > MaxOutputsLength)
                outputs = outputs.Substring(0, MaxOutputsLength) + "...";

            // A step that throws ends with an Error verdict and usually publishes nothing, so the
            // exception message is the only useful detail it has - send it along for the logs.
            var error = stepRun.Exception?.Message ?? "";
            if (error.Length > MaxOutputsLength)
                error = error.Substring(0, MaxOutputsLength) + "...";

            var stepStatus = new StepStatusMessage
            {
                Name = name ?? stepRun.TestStepName,
                Verdict = stepRun.Verdict.ToString(),
                Outputs = outputs,
                Error = error,
            };
            Send(stepStatus.ToLine());

            stepNames.Remove(stepRun.Id);
            stepOutputs.Remove(stepRun.Id);
        }

        void UpdateLiveStatus(TestStepRun stepRun)
        {
            var verdict = stepRun.Verdict;

            // Failures nested inside this step are settled now that it has finished: worse than
            // its verdict = cleared by it; otherwise it carries them on as its own (below). An
            // aborted step clears nothing - the run is ending anyway.
            var nested = verdict == Verdict.Aborted
                ? new List<ActiveFailure>()
                : activeFailures.Where(f => f.Ancestors.Contains(stepRun.Id)).ToList();
            foreach (var failure in nested)
            {
                activeFailures.Remove(failure);
                if (failure.Verdict > verdict)
                {
                    if (failure.Verdict == Verdict.Error) errorResolved = true;
                    else failResolved = true;
                }
            }

            if (verdict == Verdict.Fail || verdict == Verdict.Error)
            {
                var ancestors = new HashSet<Guid>();
                for (var id = stepRun.Parent; stepParents.TryGetValue(id, out var parent); id = parent)
                    ancestors.Add(id);
                activeFailures.Add(new ActiveFailure { Verdict = verdict, Ancestors = ancestors });
            }
            else if (verdict == Verdict.Pass)
            {
                sawPass = true;
            }

            stepParents.Remove(stepRun.Id);

            var live = new LiveStatusMessage
            {
                Passing = sawPass && activeFailures.Count == 0,
                Fail = activeFailures.Any(f => f.Verdict == Verdict.Fail) ? LiveFailureState.Active
                    : failResolved ? LiveFailureState.Resolved : LiveFailureState.None,
                Error = activeFailures.Any(f => f.Verdict == Verdict.Error) ? LiveFailureState.Active
                    : errorResolved ? LiveFailureState.Resolved : LiveFailureState.None,
            };
            if (!live.Equals(lastLive))
            {
                lastLive = live;
                Send(live.ToLine());
            }
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
