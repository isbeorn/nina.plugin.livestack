using Moq;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.QualityGate;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class QualityGateProfileTests {
        [Test]
        public async Task SwitchingProfilesReloadsGatesAndDetachesPreviousInstances() {
            await using CaptureTestContext host = new();
            IProfile original = host.Profile.Object.ActiveProfile;
            host.Plugin.PluginSettings.SetValueString("QualityGates", Gates(2));
            host.Profile.Raise(p => p.ProfileChanged += null, EventArgs.Empty);
            HFRAbsoluteGate oldGate = (HFRAbsoluteGate)host.Dockable.QualityGates.Single();
            Assert.That(oldGate.Value, Is.EqualTo(2));

            Mock<IProfile> next = new() { DefaultValue = DefaultValue.Mock };
            next.SetupGet(p => p.PluginSettings).Returns(new PluginSettings());
            host.Profile.SetupGet(p => p.ActiveProfile).Returns(next.Object);
            host.Plugin.PluginSettings.SetValueString("QualityGates", Gates(9));
            host.Profile.Raise(p => p.ProfileChanged += null, EventArgs.Empty);
            Assert.That(host.Dockable.QualityGates.Single().Value, Is.EqualTo(9));
            oldGate.Value = 4;
            Assert.That(ReadSaved(host), Is.EqualTo(9));
            ((HFRAbsoluteGate)host.Dockable.QualityGates.Single()).Value = 5;
            Assert.That(ReadSaved(host), Is.EqualTo(5));

            host.Profile.SetupGet(p => p.ActiveProfile).Returns(original);
            host.Profile.Raise(p => p.ProfileChanged += null, EventArgs.Empty);
            Assert.That(host.Dockable.QualityGates.Single().Value, Is.EqualTo(2));
            HFRAbsoluteGate removed = (HFRAbsoluteGate)host.Dockable.QualityGates.Single();
            SynchronizationContext? context = SynchronizationContext.Current;
            try {
                SynchronizationContext.SetSynchronizationContext(null);
                host.Dockable.DeleteQualityGateCommand.Execute(removed);
            } finally {
                SynchronizationContext.SetSynchronizationContext(context);
            }
            removed.Value = 8;
            Assert.That(host.Plugin.PluginSettings.GetValueString("QualityGates", "").FromStringToList<IQualityGate>(), Is.Empty);
        }

        [Test]
        public async Task DisposalDetachesProfileGatesAndBrokerSubscriptions() {
            await using CaptureTestContext host = new();
            host.Plugin.PluginSettings.SetValueString("QualityGates", Gates(2));
            host.Profile.Raise(p => p.ProfileChanged += null, EventArgs.Empty);
            HFRAbsoluteGate gate = (HFRAbsoluteGate)host.Dockable.QualityGates.Single();
            host.Dockable.Dispose();
            host.Dockable.Dispose();
            gate.Value = 7;
            Assert.That(ReadSaved(host), Is.EqualTo(2));
            host.Plugin.PluginSettings.SetValueString("QualityGates", Gates(9));
            host.Profile.Raise(p => p.ProfileChanged += null, EventArgs.Empty);
            Assert.That(host.Dockable.QualityGates.Single(), Is.SameAs(gate));
            host.Broker.Verify(b => b.Unsubscribe("Livestack_LivestackDockable_StartLiveStack", host.Dockable), Times.Once);
            host.Broker.Verify(b => b.Unsubscribe("Livestack_LivestackDockable_StopLiveStack", host.Dockable), Times.Once);
        }

        private static string Gates(double value) => new List<IQualityGate> { new HFRAbsoluteGate { Value = value } }.FromListToString();
        private static double ReadSaved(CaptureTestContext host) => host.Plugin.PluginSettings.GetValueString("QualityGates", "").FromStringToList<IQualityGate>().Single().Value;
    }
}
