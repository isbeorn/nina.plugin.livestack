using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;

namespace NINA.Plugin.Livestack.Replay {
    public partial class MainWindow : Window {
        private bool closed;
        private bool closing;
        public MainWindow(ReplayViewModel model) {
            InitializeComponent();
            DataContext = model;
        }
        private ReplayViewModel Model => (ReplayViewModel)DataContext;

        private async void AddFiles(object sender, RoutedEventArgs e) {
            OpenFileDialog dialog = new() { Multiselect = true, Filter = "Capture images|*.fits;*.fit;*.fits.fz;*.xisf", Title = "Select capture frames" };
            if (dialog.ShowDialog(this) == true) await Model.AddPathsAsync(dialog.FileNames);
        }

        private async void AddFolder(object sender, RoutedEventArgs e) {
            OpenFolderDialog dialog = new() { Title = "Select a LIGHT folder (includes subfolders)", Multiselect = true };
            if (dialog.ShowDialog(this) == true) await Model.AddPathsAsync(dialog.FolderNames);
        }

        private async void AddPaths(object sender, RoutedEventArgs e) {
            await Model.AddPathsAsync(Model.PathText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        private async void OnDrop(object sender, DragEventArgs e) {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Model.AddPathsAsync(paths);
        }

        private async void AddMasters(object sender, RoutedEventArgs e) {
            OpenFileDialog dialog = new() { Multiselect = true, Filter = "FITS calibration masters|*.fits;*.fit;*.fits.fz" };
            if (dialog.ShowDialog(this) == true) await Model.AddMastersAsync(dialog.FileNames);
        }

        protected override async void OnClosing(CancelEventArgs e) {
            base.OnClosing(e);
            if (closed) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            IsEnabled = false;
            try {
                await Model.DisposeAsync();
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "Replay cleanup", MessageBoxButton.OK, MessageBoxImage.Warning);
            } finally {
                closed = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }
}
