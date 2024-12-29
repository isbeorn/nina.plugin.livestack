using NINA.Image.Interfaces;
using OxyPlot;
using System;
using System.Collections.Immutable;

namespace NINA.Plugin.Livestack.Image {

    /// <summary>
    /// A thin wrapper to only include stats required for ColorRemappingFilter
    /// </summary>
    internal class MedianOnlyStatistics : IImageStatistics {

        public MedianOnlyStatistics(double median, double mad, int bitDepth) {
            Median = median;
            MedianAbsoluteDeviation = mad;
            BitDepth = bitDepth;
        }

        public int BitDepth { get; }

        public double StDev => throw new NotImplementedException();

        public double Mean => throw new NotImplementedException();

        public double Median { get; }

        public double MedianAbsoluteDeviation { get; }

        public int Max => throw new NotImplementedException();

        public long MaxOccurrences => throw new NotImplementedException();

        public int Min => throw new NotImplementedException();

        public long MinOccurrences => throw new NotImplementedException();

        public ImmutableList<DataPoint> Histogram => throw new NotImplementedException();
    }
}