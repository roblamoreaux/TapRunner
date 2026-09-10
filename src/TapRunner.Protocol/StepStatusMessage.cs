namespace TapRunner.Protocol
{
    /// <summary>
    /// Reports the most recently completed test step: its name, verdict, and a short one-line
    /// summary of any results it published (e.g. "Voltage=3.301, Current=0.502").
    ///
    /// Sent as a single line: "STEP|&lt;name&gt;|&lt;verdict&gt;|&lt;outputs&gt;". This is a simple
    /// pipe-delimited line protocol (not general-purpose data interchange), so name/verdict/outputs
    /// are sanitized to a single line with no '|' characters before sending.
    /// </summary>
    public class StepStatusMessage
    {
        public string Name { get; set; } = "";
        public string Verdict { get; set; } = "";
        public string Outputs { get; set; } = "";

        public string ToLine()
            => $"{StatusProtocol.StepPrefix}{Sanitize(Name)}|{Sanitize(Verdict)}|{Sanitize(Outputs)}";

        public static bool TryParse(string line, out StepStatusMessage message)
        {
            message = null;
            if (line == null || !line.StartsWith(StatusProtocol.StepPrefix))
                return false;

            var parts = line.Substring(StatusProtocol.StepPrefix.Length).Split(new[] { '|' }, 3);
            if (parts.Length < 2)
                return false;

            message = new StepStatusMessage
            {
                Name = parts[0],
                Verdict = parts[1],
                Outputs = parts.Length > 2 ? parts[2] : "",
            };
            return true;
        }

        static string Sanitize(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
    }
}
