using System.Windows;

namespace NINA.Plugin.Livestack.Replay {
    public partial class App : Application {
        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);
            MainWindow = new MainWindow(new ReplayViewModel());
            if (e.Args.Length > 0) {
                MainWindow.Loaded += async (_, _) => {
                    ReplayViewModel model = (ReplayViewModel)MainWindow.DataContext;
                    await model.AddPathsAsync(e.Args.Where(path => path != "--paused"));
                    if (!e.Args.Contains("--paused")) await model.PlayCommand.ExecuteAsync(null);
                };
            }
            MainWindow.Show();
        }
    }
}
