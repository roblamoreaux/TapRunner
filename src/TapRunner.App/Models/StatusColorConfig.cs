namespace TapRunner.App.Models
{
    /// <summary>Color (and optional flashing) configuration for one status circle.</summary>
    public class StatusColorConfig
    {
        /// <summary>Color as a hex string, e.g. "#0000FF". Parsed with ColorConverter.</summary>
        public string Color { get; set; } = "#808080";

        /// <summary>Whether the circle should flash while in this state.</summary>
        public bool Flash { get; set; }
    }

    /// <summary>The six status circle colors, persisted in AppConfig.</summary>
    public class StatusColorsConfig
    {
        public StatusColorConfig Ready { get; set; } = new() { Color = "#0000FF", Flash = false };
        public StatusColorConfig Running { get; set; } = new() { Color = "#00C000", Flash = true };
        public StatusColorConfig Passed { get; set; } = new() { Color = "#00A000", Flash = false };
        public StatusColorConfig Failed { get; set; } = new() { Color = "#FFA500", Flash = false };
        public StatusColorConfig Error { get; set; } = new() { Color = "#FF0000", Flash = false };
        public StatusColorConfig Aborted { get; set; } = new() { Color = "#8B0000", Flash = false };
    }
}
