namespace TapRunner.Protocol
{
    /// <summary>
    /// Wire protocol shared between the TapRunner.App WPF application and the
    /// TapRunner.StatusListener OpenTAP ResultListener plugin.
    ///
    /// The app creates a named pipe (PipeDirection.InOut) before launching "tap.exe run"/"tap.exe tui",
    /// passes the pipe name to the child process via the <see cref="PipeNameEnvironmentVariable"/>
    /// environment variable, and the plugin (running inside the tap.exe process) connects to it as a client.
    ///
    /// Messages are newline-terminated, case-sensitive, UTF-8 text lines. Each direction is used
    /// independently on the same duplex pipe:
    ///   - Plugin -> App: one of the Status* constants, reporting run progress/verdict.
    ///   - App -> Plugin: CommandStop, requesting a graceful (cooperative) abort of the running plan.
    /// </summary>
    public static class StatusProtocol
    {
        /// <summary>
        /// Environment variable set on the "tap.exe" child process so the StatusListener plugin
        /// knows which named pipe to connect to. If unset, the plugin does nothing (it was not
        /// launched by TapRunner.App).
        /// </summary>
        public const string PipeNameEnvironmentVariable = "TAPRUNNER_PIPE";

        // ----- Plugin -> App: status messages -----

        /// <summary>The test plan run has started.</summary>
        public const string StatusRunning = "STATUS|RUNNING";

        /// <summary>A step failed while the run is still in progress (pending overall verdict).</summary>
        public const string StatusStepFailPending = "STATUS|STEP_FAIL_PENDING";

        /// <summary>A step errored while the run is still in progress (pending overall verdict).</summary>
        public const string StatusStepErrorPending = "STATUS|STEP_ERROR_PENDING";

        /// <summary>The test plan run completed with a Pass verdict.</summary>
        public const string StatusPassed = "STATUS|PASSED";

        /// <summary>The test plan run completed with a Fail verdict.</summary>
        public const string StatusFailed = "STATUS|FAILED";

        /// <summary>The test plan run completed with an Error verdict.</summary>
        public const string StatusError = "STATUS|ERROR";

        /// <summary>The test plan run completed with an Aborted verdict.</summary>
        public const string StatusAborted = "STATUS|ABORTED";

        // ----- App -> Plugin: command messages -----

        /// <summary>Request a graceful/cooperative abort of the currently running test plan.</summary>
        public const string CommandStop = "CMD|STOP";
    }
}
