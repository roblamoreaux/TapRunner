namespace TapRunner.Protocol
{
    /// <summary>
    /// Reports that a test step has just started running - sent as "STEP_START|&lt;name&gt;" as soon
    /// as it begins, before its verdict/outputs are known. This is what drives the app's live
    /// "currently running" indicator, separate from the completed-step history that
    /// <see cref="StepStatusMessage"/> (sent once the step finishes) builds.
    /// </summary>
    public class StepStartedMessage
    {
        public string Name { get; set; } = "";

        public string ToLine() => $"{StatusProtocol.StepStartedPrefix}{Sanitize(Name)}";

        public static bool TryParse(string line, out StepStartedMessage message)
        {
            message = null;
            if (line == null || !line.StartsWith(StatusProtocol.StepStartedPrefix))
                return false;

            message = new StepStartedMessage { Name = line.Substring(StatusProtocol.StepStartedPrefix.Length) };
            return true;
        }

        static string Sanitize(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace("\r", " ").Replace("\n", " ");
    }
}
