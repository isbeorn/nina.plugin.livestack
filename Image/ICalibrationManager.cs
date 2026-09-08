using System;

using System.Threading;

namespace NINA.Plugin.Livestack.Image {

    public interface ICalibrationManager : IDisposable {

        float[] ApplyFlatFrameCalibrationInPlace(CFitsioFITSReader image, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered);

        float[] ApplyLightFrameCalibrationInPlace(CFitsioFITSReader image, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered);

        void ApplyLightFrameCalibrationInto(CFitsioFITSReader image, float[] destination, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token = default) {
            token.ThrowIfCancellationRequested();
            ApplyLightFrameCalibrationInPlace(image, width, height, exposureTime, gain, offset, inFilter, isBayered).CopyTo(destination, 0);
        }

        void ApplyFlatFrameCalibrationInto(CFitsioFITSReader image, float[] destination, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token = default) {
            token.ThrowIfCancellationRequested();
            ApplyFlatFrameCalibrationInPlace(image, width, height, exposureTime, gain, offset, inFilter, isBayered).CopyTo(destination, 0);
        }

        void Dispose();

        void RegisterBiasMaster(CalibrationFrameMeta calibrationFrameMeta);

        void RegisterDarkMaster(CalibrationFrameMeta calibrationFrameMeta);

        void RegisterFlatMaster(CalibrationFrameMeta calibrationFrameMeta);
    }
}