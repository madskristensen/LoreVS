using System;
using System.Globalization;
using LoreVS.SourceControl;

namespace LoreVS.UI
{
    /// <summary>
    /// A single revision row shown in the Lore History window. Wraps a
    /// <see cref="LoreRevisionEntry"/> from the worker and exposes display-ready, formatting-only
    /// members (short hash, first line of the message, local timestamp) so the presentation logic
    /// stays out of the view. Pure and free of Visual Studio types, so it is unit-testable.
    /// </summary>
    public sealed class LoreRevisionItem
    {
        /// <summary>Number of leading hex characters shown as the abbreviated revision id.</summary>
        private const int ShortHashLength = 8;

        public LoreRevisionItem(LoreRevisionEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            RevisionHash = entry.RevisionHash ?? string.Empty;
            ParentRevisionHash = entry.ParentRevisionHash ?? string.Empty;
            RevisionNumber = entry.RevisionNumber;
            Message = entry.Message ?? string.Empty;
            Author = entry.Author ?? string.Empty;
            BranchName = entry.BranchName ?? string.Empty;
            TimestampUnixMs = entry.TimestampUnixMs;
        }

        /// <summary>Full lowercase hex revision hash.</summary>
        public string RevisionHash { get; }

        /// <summary>Direct parent revision hash, or empty for a root revision.</summary>
        public string ParentRevisionHash { get; }

        /// <summary>Monotonic local revision number.</summary>
        public long RevisionNumber { get; }

        /// <summary>The full commit message (may be multiple lines).</summary>
        public string Message { get; }

        /// <summary>The revision author, or empty when unknown.</summary>
        public string Author { get; }

        /// <summary>The branch the revision was read from, or empty when unknown.</summary>
        public string BranchName { get; }

        /// <summary>Commit time as Unix milliseconds (UTC), or 0 when unknown.</summary>
        public long TimestampUnixMs { get; }

        /// <summary>True when this revision has a parent to diff a changed file against.</summary>
        public bool HasParent => !string.IsNullOrEmpty(ParentRevisionHash);

        /// <summary>The abbreviated revision hash shown in the list (empty when unknown).</summary>
        public string ShortHash =>
            RevisionHash.Length <= ShortHashLength ? RevisionHash : RevisionHash.Substring(0, ShortHashLength);

        /// <summary>The first non-empty line of the message, used as the row's primary caption.</summary>
        public string MessageFirstLine
        {
            get
            {
                if (string.IsNullOrEmpty(Message))
                {
                    return "(no message)";
                }

                foreach (string line in Message.Split('\n'))
                {
                    string trimmed = line.Trim('\r', ' ', '\t');
                    if (trimmed.Length > 0)
                    {
                        return trimmed;
                    }
                }

                return "(no message)";
            }
        }

        /// <summary>The commit time formatted in the local time zone, or empty when unknown.</summary>
        public string LocalTimestamp
        {
            get
            {
                if (TimestampUnixMs <= 0)
                {
                    return string.Empty;
                }

                DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(TimestampUnixMs).LocalDateTime;
                return local.ToString("g", CultureInfo.CurrentCulture);
            }
        }

        /// <summary>Secondary caption combining author and timestamp for the row subtitle.</summary>
        public string AuthorAndTime
        {
            get
            {
                string time = LocalTimestamp;
                bool hasAuthor = !string.IsNullOrEmpty(Author);
                bool hasTime = time.Length > 0;

                if (hasAuthor && hasTime)
                {
                    return Author + "  -  " + time;
                }

                return hasAuthor ? Author : time;
            }
        }

        /// <summary>Full detail shown as the row tooltip.</summary>
        public string RowToolTip
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("Revision ").Append(RevisionNumber);
                if (!string.IsNullOrEmpty(RevisionHash))
                {
                    sb.Append(" (").Append(RevisionHash).Append(')');
                }

                if (!string.IsNullOrEmpty(Author))
                {
                    sb.Append('\n').Append("Author: ").Append(Author);
                }

                string time = LocalTimestamp;
                if (time.Length > 0)
                {
                    sb.Append('\n').Append("Date: ").Append(time);
                }

                if (!string.IsNullOrEmpty(BranchName))
                {
                    sb.Append('\n').Append("Branch: ").Append(BranchName);
                }

                if (!string.IsNullOrEmpty(Message))
                {
                    sb.Append('\n').Append('\n').Append(Message.Replace("\r\n", "\n").TrimEnd('\n'));
                }

                return sb.ToString();
            }
        }
    }
}
