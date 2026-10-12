using System;
using System.Threading;

namespace NINA.Plugin.Livestack.Image {

    public interface ICalibrationManager : IDisposable {

        void ApplyLightFrameCalibrationInto(CFitsioFITSReader image, float[] destination, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token = default);

        void ApplyFlatFrameCalibrationInto(CFitsioFITSReader image, float[] destination, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token = default);

        void RegisterBiasMaster(CalibrationFrameMeta calibrationFrameMeta);
        void ClearRegisteredMasters();

        void RegisterDarkMaster(CalibrationFrameMeta calibrationFrameMeta);

        void RegisterFlatMaster(CalibrationFrameMeta calibrationFrameMeta);
    }
}
