using Accord;

namespace nina.plugin.livestack.benchmark {
    internal static class LocalTriangleProposal {
        public static List<(Point Ref, Point Src, int Votes)> Match(List<Point> reference, List<Point> current, CancellationToken token) {
            if (reference.Count > 72 || current.Count > 72) throw new ArgumentException("Local triangle experiment requires at most 72 spatially selected points.");
            List<Triangle> referenceTriangles = Build(reference, token);
            List<Triangle> currentTriangles = Build(current, token);
            int[,] votes = new int[reference.Count, current.Count];
            foreach (Triangle target in currentTriangles) {
                token.ThrowIfCancellationRequested();
                List<(Triangle Triangle, double Distance)> closest = new(4);
                foreach (Triangle source in referenceTriangles) {
                    double dx = source.X - target.X, dy = source.Y - target.Y;
                    double distance = dx * dx + dy * dy;
                    if (distance > 0.02 * 0.02) continue;
                    int index = closest.FindIndex(value => distance < value.Distance);
                    if (index < 0) index = closest.Count;
                    if (index >= 3) continue;
                    closest.Insert(index, (source, distance));
                    if (closest.Count > 3) closest.RemoveAt(3);
                }
                for (int rank = 0; rank < closest.Count; rank++) {
                    Triangle source = closest[rank].Triangle;
                    int weight = 3 - rank;
                    votes[source.A, target.A] += weight;
                    votes[source.B, target.B] += weight;
                    votes[source.C, target.C] += weight;
                }
            }
            List<(int Reference, int Current, int Votes)> ranked = new();
            for (int r = 0; r < reference.Count; r++) {
                for (int c = 0; c < current.Count; c++) {
                    if (votes[r, c] > 0) ranked.Add((r, c, votes[r, c]));
                }
            }
            ranked.Sort((left, right) => {
                int result = right.Votes.CompareTo(left.Votes);
                if (result == 0) result = left.Reference.CompareTo(right.Reference);
                return result != 0 ? result : left.Current.CompareTo(right.Current);
            });
            bool[] usedReference = new bool[reference.Count], usedCurrent = new bool[current.Count];
            List<(Point Ref, Point Src, int Votes)> pairs = new();
            foreach ((int r, int c, int score) in ranked) {
                if (usedReference[r] || usedCurrent[c]) continue;
                usedReference[r] = true;
                usedCurrent[c] = true;
                pairs.Add((reference[r], current[c], score));
            }
            return pairs;
        }

        private static List<Triangle> Build(List<Point> points, CancellationToken token) {
            List<Triangle> triangles = new();
            HashSet<int> seen = new();
            for (int anchor = 0; anchor < points.Count; anchor++) {
                token.ThrowIfCancellationRequested();
                int a = anchor;
                int[] neighbors = Enumerable.Range(0, points.Count).Where(index => index != a)
                    .OrderBy(index => DistanceSquared(points[a], points[index])).ThenBy(index => index).Take(4).ToArray();
                for (int i = 0; i < neighbors.Length; i++) {
                    for (int j = i + 1; j < neighbors.Length; j++) {
                        int[] indices = { a, neighbors[i], neighbors[j] };
                        Array.Sort(indices);
                        if (!seen.Add(indices[0] * 72 * 72 + indices[1] * 72 + indices[2])) continue;
                        (int Vertex, double Opposite)[] sides = {
                            (a, Math.Sqrt(DistanceSquared(points[neighbors[i]], points[neighbors[j]]))),
                            (neighbors[i], Math.Sqrt(DistanceSquared(points[a], points[neighbors[j]]))),
                            (neighbors[j], Math.Sqrt(DistanceSquared(points[a], points[neighbors[i]])))
                        };
                        Array.Sort(sides, (left, right) => left.Opposite.CompareTo(right.Opposite));
                        double longest = sides[2].Opposite;
                        if (longest < 12 || sides[0].Opposite / longest < 0.05) continue;
                        triangles.Add(new(sides[0].Vertex, sides[1].Vertex, sides[2].Vertex, sides[0].Opposite / longest, sides[1].Opposite / longest));
                    }
                }
            }
            return triangles;
        }

        private static double DistanceSquared(Point left, Point right) {
            double dx = (double)left.X - right.X, dy = (double)left.Y - right.Y;
            return dx * dx + dy * dy;
        }
        private sealed record Triangle(int A, int B, int C, double X, double Y);
    }
}