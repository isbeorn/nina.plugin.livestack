using BenchmarkDotNet.Attributes;
using Moq;
using NINA.Core.Interfaces;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace nina.plugin.livestack.benchmark {

    public class PluggableBehaviorSelector<T, DefaultT> : BaseINPC, IPluggableBehaviorSelector<T>
        where T : class, IPluggableBehavior
        where DefaultT : T {
        private readonly DefaultT ninaDefault;
        private string selectedContentId;

        public PluggableBehaviorSelector(DefaultT ninaDefault) {
            this.ninaDefault = ninaDefault;
            Behaviors = new AsyncObservableCollection<T>();
            Behaviors.Add(ninaDefault);
        }

        private void DetectSelectedBehaviorChanged() {
            SelectedBehaviorChanged?.Invoke(this, new EventArgs());
            selectedContentId = "";
        }

        public Type GetInterfaceType() {
            return typeof(T);
        }

        private void Behaviors_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) {
            RaisePropertyChanged(nameof(Behaviors));
            RaisePropertyChanged(nameof(SelectedBehavior));
        }

        private AsyncObservableCollection<T> behaviors;

        public event EventHandler SelectedBehaviorChanged;

        public AsyncObservableCollection<T> Behaviors {
            get => behaviors;
            set {
                if (behaviors != value) {
                    if (behaviors != null) {
                        behaviors.CollectionChanged -= Behaviors_CollectionChanged;
                    }
                    behaviors = value;
                    if (behaviors != null) {
                        behaviors.CollectionChanged += Behaviors_CollectionChanged;
                    }
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedBehavior));
                }
            }
        }

        public T SelectedBehavior {
            get => GetBehavior();
            set {
                if (value == null) {
                    throw new ArgumentException("SelectedBehavior cannot be set to null", "SelectedBehavior");
                }
                if (!Behaviors.Any(b => b.ContentId == value.ContentId)) {
                    throw new ArgumentException($"{value.ContentId} is not a plugged {typeof(T).FullName} behavior", "SelectedBehavior");
                }
            }
        }

        public T GetBehavior(string pluggableBehaviorContentId) {
            if (String.IsNullOrEmpty(pluggableBehaviorContentId)) {
                return ninaDefault;
            }

            var selected = behaviors.FirstOrDefault(b => b.ContentId == pluggableBehaviorContentId);
            if (selected != null) {
                return selected;
            }
            return ninaDefault;
        }

        public T GetBehavior() {
            return GetBehavior(null);
        }

        public void AddBehavior(object behavior) {
            var typedBehavior = behavior as T;
            if (behavior == null) {
                throw new ArgumentException($"Can't add behavior {behavior.GetType().FullName} since it doesn't implement {typeof(T).FullName}");
            }

            Behaviors.Add(typedBehavior);
        }
    }

    [MemoryDiagnoser]
    [DisassemblyDiagnoser(maxDepth: 2)]
    public class AlignmentBench {

        [Params("BenchmarkData\\light_b_1.fits")]
        public string BaseImage { get; set; }

        [Params("BenchmarkData\\light_b_2.fits")]
        public string ToBeAlignedImage { get; set; }

        private IImageDataFactory dataFactory;
        private Mock<IProfileService> profileServiceMock;

        [GlobalSetup]
        public async Task Setup() {
            var profileMock = new Mock<IProfileService>();
            string outValue = "";
            profileMock.Setup(m => m.ActiveProfile.PluginSettings.TryGetValue(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    out outValue))
                .Returns(true);
            profileMock.SetupGet(x => x.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio).Returns(1);
            profileMock.SetupGet(x => x.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio).Returns(1);
            profileMock.SetupGet(x => x.ActiveProfile.FocuserSettings.AutoFocusUseBrightestStars).Returns(0);
            profileMock.SetupGet(x => x.ActiveProfile.ImageSettings.AnnotateUnlimitedStars).Returns(false);

            var dataFactoryMock = new Mock<IImageDataFactory>();
            var windowMock = new Mock<IWindowServiceFactory>();
            var plugin = new Livestack(profileMock.Object, dataFactoryMock.Object, windowMock.Object);
            LivestackMediator.RegisterPlugin(plugin);

            dataFactory = new ImageDataFactory(profileMock.Object, new PluggableBehaviorSelector<IStarDetection, StarDetection>(new StarDetection()), new PluggableBehaviorSelector<IStarAnnotator, StarAnnotator>(new StarAnnotator()));

            // Base Image
            var image = await BaseImageData.FromFile(Path.GetFullPath(BaseImage), 16, false, null, dataFactory);

            var render = image.RenderImage();
            render = await render.Stretch(0.2, -2.8, false);
            render = await render.DetectStars(false, NINA.Core.Enum.StarSensitivityEnum.High, NINA.Core.Enum.NoiseReductionEnum.None, default, default);
            referenceImage = render;

            var toAlignImage = await BaseImageData.FromFile(Path.GetFullPath(ToBeAlignedImage), 16, false, null, dataFactory);

            // To Be Aligned Image
            var toAlignImageRender = toAlignImage.RenderImage();
            toAlignImageRender = await toAlignImageRender.Stretch(0.2, -2.8, false);
            toAlignImageRender = await toAlignImageRender.DetectStars(false, NINA.Core.Enum.StarSensitivityEnum.High, NINA.Core.Enum.NoiseReductionEnum.None, default, default);
            alignImage = toAlignImageRender;

            using (CFitsioFITSReader reader = new CFitsioFITSReader(ToBeAlignedImage)) {
                alignArray = new CalibrationManagerSimd().ApplyLightFrameCalibrationInPlace(reader, toAlignImage.Properties.Width, toAlignImage.Properties.Height, 0, -1, -1, -1, -1, "", false);
            }
        }

        private IRenderedImage referenceImage;
        private float[] alignArray;
        private IRenderedImage alignImage;

        [Benchmark]
        public void AlignFrame() {
            IImageTransformer transformer = ImageTransformer2.Instance;
            var referenceStars = transformer.GetStars(referenceImage.RawImageData.StarDetectionAnalysis.StarList, referenceImage.RawImageData.Properties.Width, referenceImage.RawImageData.Properties.Height);

            var stars = transformer.GetStars(alignImage.RawImageData.StarDetectionAnalysis.StarList, alignImage.RawImageData.Properties.Width, alignImage.RawImageData.Properties.Height);
            var affineTransformationMatrix = transformer.ComputeAffineTransformation(stars, referenceStars);
            var transformedImage = transformer.ApplyAffineTransformation(alignArray, alignImage.RawImageData.Properties.Width, alignImage.RawImageData.Properties.Height, affineTransformationMatrix, false);
        }
    }
}