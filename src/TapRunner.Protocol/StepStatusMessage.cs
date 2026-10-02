namespace TapRunner.Protocol
{
    /// <summary>
    /// Reports the most recently completed test step: its name, verdict, a short one-line summary
    /// of any results it published (e.g. "Voltage=3.301, Current=0.502"), and - when the step
    /// threw - the error message.
    ///
    /// Sent as a single line: "STEP|&lt;name&gt;|&lt;verdict&gt;|&lt;outputs&gt;|&lt;error&gt;". This is a simple
    /// pipe-delimited line protocol (not general-purpose data interchange), so every field is
    /// sanitized to a single line with no '|' characters before sending. The trailing error field
    /// is optional on the wire - a sender that predates it just omits it.
    /// </summary>
    public class StepStatusMessage
    {
        public string Name { get; set; } = "";
        public string Verdict { get; set; } = "";
        public string Outputs { get; set; } = "";

        /// <summary>The exception message if the step threw (typically an Error verdict); otherwise empty.</summary>
        public string Error { get; set; } = "";

        public string ToLine()
            => $"{StatusProtocol.StepPrefix}{Sanitize(Name)}|{Sanitize(Verdict)}|{Sanitize(Outputs)}|{Sanitize(Error)}";

        public static bool TryParse(string line, out StepStatusMessage message)
        {
            message = null;
            if (line == null || !line.StartsWith(StatusProtocol.StepPrefix))
                return false;

            var parts = line.Substring(StatusProtocol.StepPrefix.Length).Split(new[] { '|' }, 4);
            if (parts.Length < 2)
                return false;

            message = new StepStatusMessage
            {
                Name = parts[0],
                Verdict = parts[1],
                Outputs = parts.Length > 2 ? parts[2] : "",
                Error = parts.Length > 3 ? parts[3] : "",
            };
            return true;
        }

        static string Sanitize(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
    }
}
