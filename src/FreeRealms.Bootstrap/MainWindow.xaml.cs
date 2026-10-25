using System.IO;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
namespace FreeRealms.Bootstrap;
public partial class MainWindow : Window
{
    private readonly NativeServerHost nativeHost = new();
    private readonly PythonProcessHost pythonHost = new();
    private IServerHost host;
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeRealmsPS3Bootstrap", "settings.json");
    public MainWindow()
    {
        InitializeComponent();
        host = nativeHost;
        void Show(string message) => Dispatcher.InvokeAsync(() => { Logs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n"); Logs.ScrollToEnd(); });
        nativeHost.Log += Show;
        pythonHost.Log += Show;
        Loaded += (_, _) => LoadSettings();
        Closing += (_, _) => StopAll();
    }
    private void StopAll() { nativeHost.Stop(); pythonHost.Stop(); }
    private void LoadSettings()
    {
        try {
            if (!File.Exists(settingsPath)) return;
            var cfg = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            if (cfg is null) return;
            ClientPath.Text = cfg.Client; BindAddress.Text = cfg.Bind; AdvertiseAddress.Text = cfg.Advertise;
            HttpPort.Text = cfg.Port.ToString(); DnsEnabled.IsChecked = cfg.Dns;
            MatchLocal.IsChecked = cfg.MatchLocal; UsePython.IsChecked = cfg.UsePython;
        } catch (Exception ex) { Logs.AppendText($"Settings error: {ex.Message}\r\n"); }
    }
    private void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings { Client = ClientPath.Text, Bind = BindAddress.Text, Advertise = AdvertiseAddress.Text, Port = int.Parse(HttpPort.Text), Dns = DnsEnabled.IsChecked == true, MatchLocal = MatchLocal.IsChecked == true, UsePython = UsePython.IsChecked == true }));
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try {
            if (!IPAddress.TryParse(BindAddress.Text, out _)) throw new ArgumentException("Invalid listen address.");
            if (!IPAddress.TryParse(AdvertiseAddress.Text, out _)) throw new ArgumentException("Invalid advertised address.");
            if (!int.TryParse(HttpPort.Text, out var port) || port is < 1 or > 65535) throw new ArgumentException("Invalid HTTP port.");
            if (!File.Exists(Path.Combine(ClientPath.Text, "USRDIR", "Digests", "NPUA30048.soe"))) throw new FileNotFoundException("Select your local NPUA30048 folder containing USRDIR/Digests/NPUA30048.soe.");
            SaveSettings();
            StartButton.IsEnabled = false;
            host = UsePython.IsChecked == true ? pythonHost : nativeHost;
            await host.StartAsync(new ServerSettings(ClientPath.Text, BindAddress.Text, AdvertiseAddress.Text, port, DnsEnabled.IsChecked == true, MatchLocal.IsChecked == true));
            StopButton.IsEnabled = true; StatusText.Text = UsePython.IsChecked == true ? "● Running (Python)" : "● Running (C#)"; StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception ex) { StopAll(); StartButton.IsEnabled = true; StopButton.IsEnabled = false; StatusText.Text = "● Stopped"; MessageBox.Show(ex.Message, "Unable to start server", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { StopAll(); StartButton.IsEnabled = true; StopButton.IsEnabled = false; StatusText.Text = "● Stopped"; StatusText.Foreground = System.Windows.Media.Brushes.LightGray; }
    private void Browse_Click(object sender, RoutedEventArgs e) { var d = new OpenFolderDialog { Title="Select your NPUA30048 client directory" }; if (d.ShowDialog() == true) ClientPath.Text = d.FolderName; }
    private async void Updates_Click(object sender, RoutedEventArgs e) { await Updater.CheckAndInstallAsync(this, StopAll); }
    private sealed class Settings
    {
        public string Client { get; set; } = "";
        public string Bind { get; set; } = "127.0.0.1";
        public string Advertise { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 80;
        public bool Dns { get; set; } = true;
        public bool MatchLocal { get; set; } = true;
        public bool UsePython { get; set; }
    }
}