namespace TapRunner.Protocol
{
    /// <summary>Where one kind of failure (Fail or Error) stands while a run is in progress.</summary>
    public enum LiveFailureState
    {
        /// <summary>No step has had this verdict.</summary>
        None,

        /// <summary>A step had this verdict and every one was cleared by a parent step that finished better.</summary>
        Resolved,

        /// <summary>A step has this verdict and nothing has cleared it (yet) - it will carry to the plan's verdict.</summary>
        Active,
    }

    /// <summary>
    /// The run's live overall status, sent as "LIVE|&lt;passing 0/1&gt;|&lt;fail state&gt;|&lt;error state&gt;"
    /// whenever it changes. Unlike the per-step verdicts, this follows the step tree: a child's
    /// Fail that its parent step then clears (finishes Pass) becomes Resolved rather than staying
    /// a failure, the same way OpenTAP itself won't count it in the plan's verdict.
    /// </summary>
    public class LiveStatusMessage
    {
        /// <summary>At least one step has passed and no Fail/Error is <see cref="LiveFailureState.Active"/>.</summary>
        public bool Passing { get; set; }
        public LiveFailureState Fail { get; set; }
        public LiveFailureState Error { get; set; }

        public string ToLine() => $"{StatusProtocol.LivePrefix}{(Passing ? 1 : 0)}|{Fail}|{Error}";

        public static bool TryParse(string line, out LiveStatusMessage message)
        {
            message = null;
            if (line == null || !line.StartsWith(StatusProtocol.LivePrefix))
                return false;

            var parts = line.Substring(StatusProtocol.LivePrefix.Length).Split('|');
            if (parts.Length != 3 ||
                !System.Enum.TryParse(parts[1], out LiveFailureState fail) ||
                !System.Enum.TryParse(parts[2], out LiveFailureState error))
                return false;

            message = new LiveStatusMessage { Passing = parts[0] == "1", Fail = fail, Error = error };
            return true;
        }

        public override bool Equals(object obj)
            => obj is LiveStatusMessage o && o.Passing == Passing && o.Fail == Fail && o.Error == Error;

        public override int GetHashCode() => (Passing ? 1 : 0) | ((int)Fail << 1) | ((int)Error << 3);
    }
}
