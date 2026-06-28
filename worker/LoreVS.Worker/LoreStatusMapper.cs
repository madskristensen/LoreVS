using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;
using LoreVS.SourceControl;

namespace LoreVS.Worker
{
    /// <summary>
    /// Translates a single <c>lore status</c> file event from the native SDK into the
    /// normalized <see cref="LoreFileStatus"/> the rest of the extension consumes. This is the
    /// SDK-side counterpart to <c>LoreStatusParser</c> (which mapped the CLI's text codes) and
    /// must yield the same normalized values so the existing status tests stay meaningful.
    /// </summary>
    internal static class LoreStatusMapper
    {
        /// <summary>
        /// Maps a status file event's action and flags to a <see cref="LoreFileStatus"/>.
        /// Conflict wins over everything; otherwise the file action drives the result, with a
        /// dirty <see cref="LoreFileAction.KEEP"/> reported as <see cref="LoreFileStatus.Modified"/>.
        /// </summary>
        public static LoreFileStatus Map(LoreRepositoryStatusFileEventDataFFI file)
        {
            if (file.FlagConflict || file.FlagConflictUnresolved)
            {
                return LoreFileStatus.Conflicted;
            }

            switch (file.Action)
            {
                case LoreFileAction.ADD:
                    return LoreFileStatus.Added;
                case LoreFileAction.DELETE:
                    return LoreFileStatus.Deleted;
                case LoreFileAction.MOVE:
                case LoreFileAction.COPY:
                    return LoreFileStatus.Modified;
                case LoreFileAction.KEEP:
                default:
                    return file.FlagDirty ? LoreFileStatus.Modified : LoreFileStatus.Unchanged;
            }
        }
    }
}
