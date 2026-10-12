using Accord;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.Livestack.Image {
    /// <summary>Bounded search stars and independent support from one detector pass.</summary>
    public sealed class AlignmentStarCatalog {
        public const int MaximumMatchStars = 1000;
        public const int MaximumSupportStars = 16384;

        public AlignmentStarCatalog(IEnumerable<Point> matchStars, IEnumerable<Point> supportStars) {
            ArgumentNullException.ThrowIfNull(matchStars);
            ArgumentNullException.ThrowIfNull(supportStars);
            Point[] matches = matchStars.Distinct().Take(MaximumMatchStars).ToArray();
            Point[] support = matches.Concat(supportStars).Distinct().Take(MaximumSupportStars).ToArray();
            MatchStars = Array.AsReadOnly(matches);
            SupportStars = Array.AsReadOnly(support);
        }

        public IReadOnlyList<Point> MatchStars { get; }
        public IReadOnlyList<Point> SupportStars { get; }
    }
}
