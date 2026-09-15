using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Core.Session
{
    /// <summary>
    /// A mark in the feature tree, taken when the user sends a message, so the
    /// work done for that message can be removed as a unit.
    ///
    /// This exists because undo is not good enough. EditUndo2 returns void, so
    /// it cannot say whether it did anything (finding #4's cousin), and in
    /// practice it has left a stray sketch behind with the feature count
    /// unmoved. Recovery that is itself unreliable is worse than no recovery,
    /// because the agent then builds on top of a model it believes it cleaned.
    ///
    /// A mark is just the list of feature names that existed at the time.
    /// Reverting means deleting the features that were not on that list, newest
    /// first. Names are unique within a part, so the set difference is exact,
    /// and nothing the user built before the request can be caught by it.
    /// </summary>
    public sealed class CheckpointStore
    {
        private readonly List<string> _marked = new List<string>();

        public bool HasMark { get; private set; }

        /// <summary>When the mark was taken, for the message the agent reads.</summary>
        public DateTime MarkedAtUtc { get; private set; }

        /// <summary>How many features existed at the mark.</summary>
        public int MarkedCount => _marked.Count;

        /// <summary>Record the tree as it stands. Replaces any previous mark.</summary>
        public void Mark(IEnumerable<string> featureNames)
        {
            _marked.Clear();
            if (featureNames != null)
                _marked.AddRange(featureNames.Where(n => !string.IsNullOrEmpty(n)));

            HasMark = true;
            MarkedAtUtc = DateTime.UtcNow;
        }

        public void Clear()
        {
            _marked.Clear();
            HasMark = false;
        }

        /// <summary>
        /// Features present now that were not present at the mark, in tree
        /// order. Reverting deletes these in reverse, so a feature is never
        /// deleted before something built on top of it.
        /// </summary>
        public IReadOnlyList<string> AddedSince(IEnumerable<string> currentFeatureNames)
        {
            if (!HasMark || currentFeatureNames == null) return new List<string>();

            var before = new HashSet<string>(_marked, StringComparer.Ordinal);
            return currentFeatureNames.Where(n => !string.IsNullOrEmpty(n) && !before.Contains(n)).ToList();
        }
    }
}
