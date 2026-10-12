using Moq;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack.Replay;
using NINA.Profile.Interfaces;
using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using System.Windows.Media;
using System.Globalization;

namespace nina.plugin.livestack.benchmark {
    internal sealed class ArchiveVerificationDetector {
        private readonly IStarDetection detector;
        private readonly Mock<IStarDetection> recording = new();
        public List<DetectedStar>? LatestStars { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Target { get; private set; } = "";
        public string Filter { get; private set; } = "";
        public Dictionary<string, string> Settings { get; } = new();
        public string Version => detector.GetType().Assembly.GetName().Version?.ToString() ?? "unknown";

        public ArchiveVerificationDetector(ReplayHost host, string name, string? pluginDirectory) {
            ImageDataFactory factory = (ImageDataFactory)typeof(ReplayHost).GetField("factory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            IProfileService profile = (IProfileService)typeof(ImageDataFactory).GetField("profileService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(factory)!;
            detector = name == "builtin" ? new StarDetection() : LoadHocusFocus(profile, pluginDirectory, Settings);
            Settings["StarSensitivity"] = "High";
            Settings["NoiseReduction"] = "None";
            Settings["AutoStretchFactor"] = "0.2";
            Settings["BlackClipping"] = "-2.8";
            recording.SetupGet(value => value.Name).Returns(detector.Name);
            recording.SetupGet(value => value.ContentId).Returns(detector.ContentId);
            recording.Setup(value => value.CreateAnalysis()).Returns(() => detector.CreateAnalysis());
            recording.Setup(value => value.Detect(It.IsAny<IRenderedImage>(), It.IsAny<PixelFormat>(), It.IsAny<StarDetectionParams>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns((IRenderedImage image, PixelFormat format, StarDetectionParams parameters, IProgress<ApplicationStatus> progress, CancellationToken token) => {
                    Width = image.RawImageData.Properties.Width;
                    Height = image.RawImageData.Properties.Height;
                    double pixelSize = image.RawImageData.MetaData.Camera.PixelSize;
                    double focalLength = image.RawImageData.MetaData.Telescope.FocalLength;
                    if (double.IsFinite(pixelSize) && pixelSize > 0) profile.ActiveProfile.CameraSettings.PixelSize = pixelSize;
                    if (double.IsFinite(focalLength) && focalLength > 0) profile.ActiveProfile.TelescopeSettings.FocalLength = focalLength;
                    Settings["PixelSize"] = profile.ActiveProfile.CameraSettings.PixelSize.ToString(CultureInfo.InvariantCulture);
                    Settings["FocalLength"] = profile.ActiveProfile.TelescopeSettings.FocalLength.ToString(CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(image.RawImageData.MetaData.Target.Name)) Target = image.RawImageData.MetaData.Target.Name;
                    if (!string.IsNullOrWhiteSpace(image.RawImageData.MetaData.FilterWheel.Filter)) Filter = image.RawImageData.MetaData.FilterWheel.Filter;
                    return detector.Detect(image, format, parameters, progress, token);
                });
            recording.Setup(value => value.UpdateAnalysis(It.IsAny<IStarDetectionAnalysis>(), It.IsAny<StarDetectionParams>(), It.IsAny<StarDetectionResult>()))
                .Callback((IStarDetectionAnalysis analysis, StarDetectionParams parameters, StarDetectionResult result) => {
                    detector.UpdateAnalysis(analysis, parameters, result);
                    LatestStars = analysis.StarList;
                });
            IPluggableBehaviorSelector<IStarDetection> selector = (IPluggableBehaviorSelector<IStarDetection>)typeof(ImageDataFactory)
                .GetField("starDetectionSelector", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(factory)!;
            Mock.Get(selector).Setup(value => value.GetBehavior()).Returns(recording.Object);
        }

        public void Clear() { recording.Invocations.Clear(); LatestStars = null; Target = ""; Filter = ""; }

        private static IStarDetection LoadHocusFocus(IProfileService profile, string? pluginDirectory, Dictionary<string, string> settings) {
            string directory = pluginDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "Plugins", "3.0.0", "Hocus Focus");
            directory = Path.GetFullPath(directory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            AssemblyLoadContext.Default.Resolving += (_, name) => {
                string dependency = Path.Combine(directory, name.Name + ".dll");
                return System.IO.File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
            };
            string native = Path.Combine(directory, "dll", "x64");
            if (Directory.Exists(native)) Environment.SetEnvironmentVariable("PATH", native + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "NINA.Joko.Plugins.HocusFocus.dll"));
            Type detectorType = assembly.GetType("NINA.Joko.Plugins.HocusFocus.StarDetection.HocusFocusStarDetection", throwOnError: true)!;
            ConstructorInfo constructor = detectorType.GetConstructors().Single(value => value.GetParameters().Length == 6);
            object options = Activator.CreateInstance(assembly.GetType("NINA.Joko.Plugins.HocusFocus.StarDetection.StarDetectionOptions", true)!, profile)!;
            foreach (PropertyInfo property in options.GetType().GetProperties().Where(value => value.CanRead && value.GetIndexParameters().Length == 0)) {
                if (property.PropertyType.IsPrimitive || property.PropertyType.IsEnum || property.PropertyType == typeof(string)) {
                    settings[property.Name] = Convert.ToString(property.GetValue(options), CultureInfo.InvariantCulture) ?? "";
                }
            }
            object alglib = Activator.CreateInstance(assembly.GetType("NINA.Joko.Plugins.HocusFocus.Utility.AlglibAPI", true)!)!;
            ParameterInfo[] parameters = constructor.GetParameters();
            object MockObject(Type type) {
                Mock mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!;
                mock.DefaultValue = DefaultValue.Mock;
                return mock.Object;
            }
            return (IStarDetection)constructor.Invoke(new[] { MockObject(parameters[0].ParameterType), profile,
                MockObject(parameters[2].ParameterType), options, alglib, MockObject(parameters[5].ParameterType) });
        }
    }
}
