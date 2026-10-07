namespace TapRunner.App.Models
{
    /// <summary>Visual state of one status circle.</summary>
    public enum CircleState
    {
        /// <summary>Not lit (dim/gray).</summary>
        Off,

        /// <summary>Lit at reduced opacity - a Fail/Error that a parent step cleared (or, from an older plugin, a pending one).</summary>
        Partial,

        /// <summary>Fully lit (and flashing, if configured).</summary>
        Full,

        /// <summary>Fully lit and always flashing - the live verdict while a run is in progress.</summary>
        Flashing
    }
}
