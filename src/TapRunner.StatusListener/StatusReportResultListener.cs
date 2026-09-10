using System;
using System.IO;
using System.IO.Pipes;
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

        public StatusReportResultListener()
        {
            Name = "TapRunner Status";
        }

        public override void OnTestPlanRunStart(TestPlanRun planRun)
        {
            sawFailedStep = false;
            sawErrorStep = false;

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
