using System;
using LoreVcs.Types.Enums;
using LoreVS.SourceControl;

namespace LoreVS.Worker
{
    /// <summary>
    /// Pure helpers that translate the native Lore revision-history / revision-info FFI data into the
    /// normalized shapes the extension consumes. Kept free of the native event types (they take
    /// primitive inputs) so the mapping is unit-testable without constructing SDK events, mirroring
    /// <see cref="LoreStatusMapper"/>.
    /// </summary>
    internal static class LoreRevisionMapper
    {
        /// <summary>
        /// Maps a revision-info delta's file action and modify flag to a <see cref="LoreFileStatus"/>.
        /// A <see cref="LoreFileAction.KEEP"/> whose content changed is reported as
        /// <see cref="LoreFileStatus.Modified"/>; a clean KEEP is <see cref="LoreFileStatus.Unchanged"/>
        /// and is filtered out by the caller so only files the revision actually changed are surfaced.
        /// </summary>
        public static LoreFileStatus MapDelta(LoreFileAction action, bool flagModify)
        {
            switch (action)
            {
                case LoreFileAction.ADD:
                    return LoreFileStatus.Added;
                case LoreFileAction.DELETE:
                    return LoreFileStatus.Deleted;
                case LoreFileAction.MOVE:
                    return LoreFileStatus.Renamed;
                case LoreFileAction.COPY:
                    return LoreFileStatus.Modified;
                case LoreFileAction.KEEP:
                default:
                    return flagModify ? LoreFileStatus.Modified : LoreFileStatus.Unchanged;
            }
        }

        /// <summary>
        /// Returns the direct parent hash for a revision from the native parent slots. Lore emits a
        /// fixed-length array whose empty slots are all-zero hashes; the first non-zero slot is the
        /// direct parent, and an all-empty array means a root revision (returns an empty string).
        /// </summary>
        public static string ParentHash(byte[][]? parents)
        {
            if (parents == null)
            {
                return string.Empty;
            }

            foreach (byte[] parent in parents)
            {
                string hex = ToHex(parent);
                if (!IsEmptyHash(hex))
                {
                    return hex;
                }
            }

            return string.Empty;
        }

        /// <summary>Converts a raw hash byte array to a lowercase hex string (empty when null/empty).</summary>
        public static string ToHex(byte[]? data)
        {
            return data == null || data.Length == 0
                ? string.Empty
                : BitConverter.ToString(data).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>True when a hex hash is empty or consists solely of zeros (an unset parent slot).</summary>
        public static bool IsEmptyHash(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return true;
            }

            foreach (char c in hex)
            {
                if (c != '0')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
