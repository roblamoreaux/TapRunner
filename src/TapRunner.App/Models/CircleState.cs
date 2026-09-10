namespace TapRunner.App.Models
{
    /// <summary>Visual state of one status circle.</summary>
    public enum CircleState
    {
        /// <summary>Not lit (dim/gray).</summary>
        Off,

        /// <summary>Lit at reduced opacity - used for a pending Fail/Error while the run is still in progress.</summary>
        Partial,

        /// <summary>Fully lit (and flashing, if configured).</summary>
        Full
    }
}
