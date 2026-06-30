namespace LoreVS.SourceControl
{
    /// <summary>
    /// Normalized source control state for a single file, independent of how the
    /// underlying Lore state was obtained (CLI today, brokered SDK service later).
    /// </summary>
    public enum LoreFileStatus
    {
        /// <summary>The file is not known to Lore / not under source control.</summary>
        NotControlled = 0,

        /// <summary>Tracked and unchanged relative to the current revision.</summary>
        Unchanged,

        /// <summary>Tracked and modified in the working tree.</summary>
        Modified,

        /// <summary>Newly added/staged, not yet committed.</summary>
        Added,

        /// <summary>Removed from the working tree but still tracked.</summary>
        Deleted,

        /// <summary>Renamed or moved from another path in the working tree.</summary>
        Renamed,

        /// <summary>In a conflicted/merge state.</summary>
        Conflicted,

        /// <summary>Locked (Lore supports exclusive locks on binary assets).</summary>
        Locked,

        /// <summary>Tracked but excluded/ignored.</summary>
        Ignored,
    }
}
