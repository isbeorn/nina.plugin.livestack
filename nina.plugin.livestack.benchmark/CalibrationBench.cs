using BenchmarkDotNet.Attributes;
using MathNet.Numerics.Statistics;
using Moq;
using NINA.Core.Utility.WindowService;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.TestSupport;
using NINA.Profile.Interfaces;

[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
public class CalibrationBench {

    // Typical camera resolutions
    [Params("BenchmarkData\\light_b_1.fits")]
    public string ImagePath { get; set; }

    [Params("BenchmarkData\\flat_b.fits")]
    public string FlatPath { get; set; }

    [Params("BenchmarkData\\bias.fits")]
    public string BiasPath { get; set; }

    private int frameWidth;
    private int frameHeight;
    private double frameExposureTime;
    private int frameGain;
    private int frameOffset;
    private int frameBinX;
    private int frameBinY;
    private string frameFilter;
    private bool frameIsBayered;

    private CFitsioFITSReader reader;
    private CalibrationReference manager;
    private CalibrationManagerSimd manager2;
    private float[] reusableOutput;

    [GlobalSetup]
    public void Setup() {
        var profileMock = new Mock<IProfileService>();
        string outValue = "";
        profileMock.Setup(m => m.ActiveProfile.PluginSettings.TryGetValue(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                out outValue))
            .Returns(true);
        var dataFactoryMock = new Mock<IImageDataFactory>();
        var windowMock = new Mock<IWindowServiceFactory>();
        var plugin = new Livestack(profileMock.Object, dataFactoryMock.Object, windowMock.Object);
        LivestackMediator.RegisterPlugin(plugin);

        reader = new CFitsioFITSReader(ImagePath);

        // Readout metadata
        using (var fits = new CFitsioFITSReader(ImagePath)) {
            var metaData = fits.ReadHeader().ExtractMetaData();

            frameGain = metaData.Camera.Gain;
            frameOffset = metaData.Camera.Offset;
            frameBinX = metaData.Camera.BinX;
            frameBinY = metaData.Camera.BinY;
            frameFilter = metaData.FilterWheel.Filter;
            frameExposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
            frameWidth = fits.Width;
            frameHeight = fits.Height;
        }

        manager = new CalibrationReference();
        manager2 = new CalibrationManagerSimd(cacheMasterRows: true);
        reusableOutput = new float[frameWidth * frameHeight];

        // Register FLAT Master
        using (var fits = new CFitsioFITSReader(FlatPath)) {
            var metaData = fits.ReadHeader().ExtractMetaData();

            var imageType = metaData.Image.ImageType;
            var gain = metaData.Camera.Gain;
            var offset = metaData.Camera.Offset;
            var binX = metaData.Camera.BinX;
            var binY = metaData.Camera.BinY;
            var filter = metaData.FilterWheel.Filter;
            var exposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
            var width = fits.Width;
            var height = fits.Height;

            var mean = (float)fits.ReadAllPixelsAsFloat().Mean();

            manager.RegisterFlatMaster(new CalibrationFrameMeta(CalibrationFrameType.FLAT, FlatPath, gain, offset, binX, binY, exposureTime, filter, width, height, mean));
            manager2.RegisterFlatMaster(new CalibrationFrameMeta(CalibrationFrameType.FLAT, FlatPath, gain, offset, binX, binY, exposureTime, filter, width, height, mean));
        }

        // Register BIAS Master
        if (!string.IsNullOrEmpty(BiasPath)) {
            using (var fits = new CFitsioFITSReader(BiasPath)) {
                var metaData = fits.ReadHeader().ExtractMetaData();

                var imageType = metaData.Image.ImageType;
                var gain = metaData.Camera.Gain;
                var offset = metaData.Camera.Offset;
                var binX = metaData.Camera.BinX;
                var binY = metaData.Camera.BinY;
                var filter = metaData.FilterWheel.Filter;
                var exposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                var width = fits.Width;
                var height = fits.Height;

                var mean = (float)fits.ReadAllPixelsAsFloat().Mean();

                manager.RegisterBiasMaster(new CalibrationFrameMeta(CalibrationFrameType.BIAS, BiasPath, gain, offset, binX, binY, exposureTime, filter, width, height, mean));
                manager2.RegisterBiasMaster(new CalibrationFrameMeta(CalibrationFrameType.BIAS, BiasPath, gain, offset, binX, binY, exposureTime, filter, width, height, mean));
            }
            plugin.UseBiasForLights = true;
        }
    }

    // ------------------------------------------------------------------
    // BENCHMARKS
    // ------------------------------------------------------------------

    [Benchmark(Baseline = true)]
    public void Baseline_Calibration() {
        manager.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameBinX, frameBinY, frameFilter, frameIsBayered);
    }

    [Benchmark()]
    public void SimdAndPixelRowOpti_Calibration() {
        manager2.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameBinX, frameBinY, frameFilter, frameIsBayered);
    }

    [Benchmark]
    public float CachedMastersAndAllocatedFrame() {
        using CalibrationManagerSimd calibration = CreateFrameManager(true);
        return calibration.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameBinX, frameBinY, frameFilter, frameIsBayered)[0];
    }

    [Benchmark]
    public float StreamedMastersAndReusedFrame() {
        using CalibrationManagerSimd calibration = CreateFrameManager(false);
        calibration.ApplyLightFrameCalibrationInto(reader, reusableOutput, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameBinX, frameBinY, frameFilter, frameIsBayered);
        return reusableOutput[0];
    }

    [Benchmark]
    public float PersistentStreamedMastersAndReusedFrame() {
        manager2.ApplyLightFrameCalibrationInto(reader, reusableOutput, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameBinX, frameBinY, frameFilter, frameIsBayered);
        return reusableOutput[0];
    }

    private CalibrationManagerSimd CreateFrameManager(bool cached) {
        CalibrationManagerSimd calibration = new(cached);
        foreach (CalibrationFrameMeta master in manager2.BiasLibrary) calibration.RegisterBiasMaster(master);
        foreach (CalibrationFrameMeta master in manager2.DarkLibrary) calibration.RegisterDarkMaster(master);
        foreach (CalibrationFrameMeta master in manager2.FlatLibrary) calibration.RegisterFlatMaster(master);
        return calibration;
    }

    [GlobalCleanup]
    public void Cleanup() {
        reader.Dispose();
        manager.Dispose();
        manager2.Dispose();
    }

    /*
    [Benchmark]
    public void Optimized_RowKernel() {
        for (int r = 0; r < Height; r++) {
            int o = r * Width;
            CalibrateRow_Optimized(
                _dst.AsSpan(o, Width),
                _light.AsSpan(o, Width),
                _bias.AsSpan(o, Width),
                _dark.AsSpan(o, Width),
                _flat.AsSpan(o, Width));
        }

        _sink = _dst[(Width * Height) / 2];
    }

    [Benchmark]
    public void Simd_RowKernel_Division() {
        for (int r = 0; r < Height; r++) {
            int o = r * Width;
            CalibrateRow_Simd_Division(
                _dst.AsSpan(o, Width),
                _light.AsSpan(o, Width),
                _bias.AsSpan(o, Width),
                _dark.AsSpan(o, Width),
                _flat.AsSpan(o, Width));
        }

        _sink = _dst[(Width * Height) / 2];
    }

    [Benchmark]
    public void Simd_RowKernel_GainMap() {
        for (int r = 0; r < Height; r++) {
            int o = r * Width;
            CalibrateRow_Simd_GainMap(
                _dst.AsSpan(o, Width),
                _light.AsSpan(o, Width),
                _bias.AsSpan(o, Width),
                _dark.AsSpan(o, Width),
                _gain.AsSpan(o, Width));
        }

        _sink = _dst[(Width * Height) / 2];
    }

    // ------------------------------------------------------------------
    // KERNELS
    // ------------------------------------------------------------------

    // Baseline: matches original math (division via flat/mean)
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalibrateRow_Baseline(
        Span<float> dst,
        ReadOnlySpan<float> light,
        ReadOnlySpan<float> bias,
        ReadOnlySpan<float> dark,
        ReadOnlySpan<float> flat) {
        for (int i = 0; i < dst.Length; i++) {
            float v = light[i] - bias[i] - dark[i];

            if (v < 0f) v = 0f;
            else if (v > 1f) v = 1f;

            float flatCorrected = flat[i] / FlatMean;
            dst[i] = v / flatCorrected;
        }
    }

    // Scalar optimized: algebraic rewrite (multiply instead of divide)
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalibrateRow_Optimized(
        Span<float> dst,
        ReadOnlySpan<float> light,
        ReadOnlySpan<float> bias,
        ReadOnlySpan<float> dark,
        ReadOnlySpan<float> flat) {
        for (int i = 0; i < dst.Length; i++) {
            float v = light[i] - bias[i] - dark[i];

            if (v < 0f) v = 0f;
            else if (v > 1f) v = 1f;

            dst[i] = v * (FlatMean / flat[i]);
        }
    }

    // SIMD with division
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalibrateRow_Simd_Division(
        Span<float> dst,
        ReadOnlySpan<float> light,
        ReadOnlySpan<float> bias,
        ReadOnlySpan<float> dark,
        ReadOnlySpan<float> flat) {
        int n = dst.Length;
        int simd = Vector<float>.Count;
        int i = 0;
        int last = n - (n % simd);

        var vZero = Vector<float>.Zero;
        var vOne = new Vector<float>(1f);
        var vMean = new Vector<float>(FlatMean);

        for (; i < last; i += simd) {
            var v = new Vector<float>(light.Slice(i, simd));
            v -= new Vector<float>(bias.Slice(i, simd));
            v -= new Vector<float>(dark.Slice(i, simd));

            v = Vector.Min(vOne, Vector.Max(vZero, v));
            v *= vMean / new Vector<float>(flat.Slice(i, simd));

            v.CopyTo(dst.Slice(i, simd));
        }

        for (; i < n; i++) {
            float v = light[i] - bias[i] - dark[i];
            if (v < 0f) v = 0f;
            else if (v > 1f) v = 1f;
            dst[i] = v * (FlatMean / flat[i]);
        }
    }

    // SIMD + gain map (no division in hot loop)
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalibrateRow_Simd_GainMap(
        Span<float> dst,
        ReadOnlySpan<float> light,
        ReadOnlySpan<float> bias,
        ReadOnlySpan<float> dark,
        ReadOnlySpan<float> gain) {
        int n = dst.Length;
        int simd = Vector<float>.Count;
        int i = 0;
        int last = n - (n % simd);

        var vZero = Vector<float>.Zero;
        var vOne = new Vector<float>(1f);

        for (; i < last; i += simd) {
            var v = new Vector<float>(light.Slice(i, simd));
            v -= new Vector<float>(bias.Slice(i, simd));
            v -= new Vector<float>(dark.Slice(i, simd));

            v = Vector.Min(vOne, Vector.Max(vZero, v));
            v *= new Vector<float>(gain.Slice(i, simd));

            v.CopyTo(dst.Slice(i, simd));
        }

        for (; i < n; i++) {
            float v = light[i] - bias[i] - dark[i];
            if (v < 0f) v = 0f;
            else if (v > 1f) v = 1f;
            dst[i] = v * gain[i];
        }
    }
    */
}
