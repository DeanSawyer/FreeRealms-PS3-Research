using System.IO;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
namespace FreeRealms.Bootstrap;
public partial class MainWindow : Window
{
    private readonly PythonProcessHost host = new();
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeRealmsPS3Bootstrap", "settings.json");
    public MainWindow()
    {
        InitializeComponent();
        host.Log += message => Dispatcher.InvokeAsync(() => { Logs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n"); Logs.ScrollToEnd(); });
        Loaded += (_, _) => LoadSettings();
        Closing += (_, _) => host.Stop();
    }
    private void LoadSettings()
    {
        try {
            if (!File.Exists(settingsPath)) return;
            var cfg = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            if (cfg is null) return;
            ClientPath.Text = cfg.Client; BindAddress.Text = cfg.Bind; AdvertiseAddress.Text = cfg.Advertise;
            HttpPort.Text = cfg.Port.ToString(); DnsEnabled.IsChecked = cfg.Dns;
        } catch (Exception ex) { Logs.AppendText($"Settings error: {ex.Message}\r\n"); }
    }
    private void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings(ClientPath.Text, BindAddress.Text, AdvertiseAddress.Text, int.Parse(HttpPort.Text), DnsEnabled.IsChecked == true)));
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
            await host.StartAsync(ClientPath.Text, BindAddress.Text, AdvertiseAddress.Text, port, DnsEnabled.IsChecked == true);
            StopButton.IsEnabled = true; StatusText.Text = "● Running"; StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception ex) { host.Stop(); StartButton.IsEnabled = true; StopButton.IsEnabled = false; StatusText.Text = "● Stopped"; MessageBox.Show(ex.Message, "Unable to start server", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { host.Stop(); StartButton.IsEnabled = true; StopButton.IsEnabled = false; StatusText.Text = "● Stopped"; StatusText.Foreground = System.Windows.Media.Brushes.LightGray; }
    private void Browse_Click(object sender, RoutedEventArgs e) { var d = new OpenFolderDialog { Title="Select your NPUA30048 client directory" }; if (d.ShowDialog() == true) ClientPath.Text = d.FolderName; }
    private async void Updates_Click(object sender, RoutedEventArgs e) { await Updater.CheckAndInstallAsync(this, host.Stop); }
    private sealed record Settings(string Client, string Bind, string Advertise, int Port, bool Dns);
}