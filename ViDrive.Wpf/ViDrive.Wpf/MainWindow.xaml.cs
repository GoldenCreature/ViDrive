using System;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace ViDrive.Wpf
{
    public partial class MainWindow : Window
    {
        private UnityEmbedHost? _unityHost;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 실행 폴더(bin/Debug/net8.0-windows) 기준으로 5단계 위 = 리포 루트(ViDrive/)
            // (ViDrive.Wpf 프로젝트가 솔루션 폴더 안에 한 겹 더 중첩되어 있어 5단계가 필요함)
            string repoRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string unityExePath = Path.Combine(repoRoot, "ViDrive.Unity", "Build", "PoC1_WindowEmbed", "PoC1_WindowEmbed.exe");

            _unityHost = new UnityEmbedHost(unityExePath);
            UnityContainer.Children.Add(_unityHost);
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _unityHost?.Shutdown();
        }
    }
}