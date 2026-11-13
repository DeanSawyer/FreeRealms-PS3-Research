using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.Win32;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace FreeRealms.Bootstrap;

/// <summary>One row of the Characters page.</summary>
public sealed record CharacterRow(ulong Guid, string Name, string Detail, string Made, string LastPlayed, bool ShownNext);

/// <summary>One row of the Players page.</summary>
public sealed record PlayerRow(string OnlineId, string Character, string Address, string Job, string State, int Packets, string LastPacket, string Connected);

public partial class MainWindow : Window
{
    private const int MaxLogEntries = 20000;

    private readonly ServerController server = new();
    private readonly ObservableCollection<LogEntry> logEntries = [];
    private readonly ObservableCollection<LogEntry> recent = [];
    private readonly ObservableCollection<PlayerRow> players = [];
    private readonly ObservableCollection<CharacterRow> characterRows = [];
    private readonly ConcurrentQueue<LogEntry> incoming = new();
    private readonly DispatcherTimer logPump;
    private readonly DispatcherTimer statusTimer;
    private readonly ICollectionView logView;
    private readonly bool ready;

    private ManagerSettings settings;
    private bool paused;
    private bool busy;
    private bool closing;
    private string? lastReport;
    private string charactersShown = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        settings = ManagerSettings.Load();

        logView = CollectionViewSource.GetDefaultView(logEntries);
        logView.Filter = item => item is LogEntry entry && ShowInLog(entry);

        LogList.ItemsSource = logView;
        RecentLog.ItemsSource = recent;
        PlayersList.ItemsSource = players;
        CharactersList.ItemsSource = characterRows;

        server.Log += entry => incoming.Enqueue(entry);

        logPump = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => PumpLog(), Dispatcher);
        statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStatus(), Dispatcher);

        var version = Assembly.GetExecutingAssembly().GetName().Version;

        VersionText.Text = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";

        ShowSettings();
        RefreshStatus();

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            server.Write(LogSeverity.Warning, "Manager", "Not running as administrator: the DNS stand-in (UDP 53) and the web server on port 80 may not start.");

        server.Write(LogSeverity.Info, "Manager", $"Settings: {Path.Combine(ManagerSettings.DataFolder, "settings.json")}");

        Closing += Window_Closing;
        ready = true;
    }

    // --- Navigation ---------------------------------------------------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!ready)
            return;

        PageDashboard.Visibility = NavDashboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PagePlayers.Visibility = NavPlayers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageCharacters.Visibility = NavCharacters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageGameplay.Visibility = NavGameplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = NavLogs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDiagnostics.Visibility = NavDiagnostics.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavLogs.IsChecked == true)
            ScrollLogToEnd();

        if (NavCharacters.IsChecked == true)
            RefreshCharacters(force: true);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => NavLogs.IsChecked = true;

    // --- Characters ---------------------------------------------------------------------------------------------------

    /// <summary>The running server's characters, or the saved file when the server is stopped.</summary>
    private CharacterStore Characters() => server.Server?.Characters ?? new CharacterStore(ManagerSettings.CharacterFile);

    /// <summary>Fills the Characters page; with <paramref name="force"/> false only when something changed.</summary>
    private void RefreshCharacters(bool force)
    {
        CharacterStore store;

        try
        {
            store = Characters();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            server.Write(LogSeverity.Warning, "Manager", $"Characters could not be read: {exception.Message}");

            return;
        }

        var ordered = store.Ordered();
        var creatorNext = store.CreatorNext;
        var shownNext = creatorNext ? null : ordered.FirstOrDefault()?.Guid;
        var signature = $"{creatorNext}|{string.Join(';', ordered.Select(character => $"{character.Guid}:{character.Name}:{character.LastPlayed?.Ticks}"))}";

        if (!force && signature == charactersShown)
            return;

        charactersShown = signature;
        characterRows.Clear();

        foreach (var character in ordered)
        {
            characterRows.Add(new CharacterRow(
                character.Guid,
                character.Name.Length > 0 ? character.Name : "(no name)",
                $"guid {character.Guid}",
                When(character.Created),
                character.LastPlayed is { } played ? When(played) : "never",
                character.Guid == shownNext));
        }

        NoCharacters.Visibility = characterRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreatorNextSwitch.IsChecked = creatorNext;
        CreatorNextSwitch.IsEnabled = ordered.Count < CharacterStore.MaxCharacters || creatorNext;
        CreatorNextHint.Text = ordered.Count >= CharacterStore.MaxCharacters && !creatorNext
            ? $"This account has {CharacterStore.MaxCharacters} characters, Sanctuary's limit. Delete one to make another."
            : "Make another character; the ones below are kept. Turns itself off once the new character is made.";

        static string When(DateTime time) => time.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
    }

    private void RefreshCharacters_Click(object sender, RoutedEventArgs e) => RefreshCharacters(force: true);

    private void CreatorNext_Click(object sender, RoutedEventArgs e)
    {
        var on = CreatorNextSwitch.IsChecked == true;

        Characters().SetCreatorNext(on);
        server.Write(LogSeverity.Info, "Manager", on ? "Characters: the next login opens the character creator." : "Characters: the next login shows character select again.");
        RefreshCharacters(force: true);
    }

    private void ShowNext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ulong guid })
            return;

        var store = Characters();

        if (store.ShowNext(guid))
            server.Write(LogSeverity.Info, "Manager", $"Characters: the PS3 shows {store.Find(guid)?.Name} next time.");

        RefreshCharacters(force: true);
    }

    private void DeleteCharacter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ulong guid })
            return;

        var store = Characters();
        var name = store.Find(guid)?.Name ?? $"guid {guid}";

        if (MessageBox.Show(this, $"Delete {name}? This cannot be undone.", "Delete character", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (store.Delete(guid))
            server.Write(LogSeverity.Info, "Manager", $"Characters: deleted {name}.");

        RefreshCharacters(force: true);
    }

    // --- Server -------------------------------------------------------------------------------------------------------

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartServerAsync();

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopServerAsync();

    private async Task<bool> StartServerAsync()
    {
        if (!ReadSettings())
            return false;

        SetBusy(true);

        try
        {
            await server.StartAsync(settings);

            return true;
        }
        catch (InvalidOperationException exception)
        {
            server.Write(LogSeverity.Error, "Manager", exception.Message);
            MessageBox.Show(this, exception.Message, "The server did not start", MessageBoxButton.OK, MessageBoxImage.Warning);

            return false;
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private async Task StopServerAsync()
    {
        SetBusy(true);

        try
        {
            await server.StopAsync();
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private async void ApplyGameplay_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettings())
            return;

        if (!server.IsRunning)
        {
            server.Write(LogSeverity.Info, "Manager", "Gameplay settings saved; they apply when the server starts.");

            return;
        }

        await StopServerAsync();
        await StartServerAsync();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ReadSettings())
            server.Write(LogSeverity.Info, "Manager", server.IsRunning ? "Settings saved; restart the server to use them." : "Settings saved.");
    }

    // --- Test runs and diagnostics ------------------------------------------------------------------------------------

    private async void TestRun_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettings())
            return;

        SetBusy(true, testRun: true);

        try
        {
            var zip = await Reports.TestRunAsync(settings, server, () => logEntries.ToList(), text => server.Write(LogSeverity.Info, "Test run", text));

            ShowReport(zip);
            MessageBox.Show(this, $"The report is saved:\n\n{zip}\n\nAttach this zip and say what the screen showed.", "Test run finished", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            server.Write(LogSeverity.Error, "Test run", exception.Message);
            MessageBox.Show(this, exception.Message, "Test run", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var zip = Reports.Diagnostics(settings, logEntries.ToList());

            ShowReport(zip);
            server.Write(LogSeverity.Info, "Manager", $"Diagnostics saved to {zip}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Export diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowReport(string zip)
    {
        lastReport = zip;
        LastReportText.Text = zip;
        LastReportCard.Visibility = Visibility.Visible;
    }

    private void ShowReport_Click(object sender, RoutedEventArgs e)
    {
        if (lastReport is not null && File.Exists(lastReport))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastReport}\"") { UseShellExecute = true });
    }

    private void OpenTraces_Click(object sender, RoutedEventArgs e) => OpenFolder(ManagerSettings.TraceFolder);

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(ManagerSettings.DataFolder);

    private async void Updates_Click(object sender, RoutedEventArgs e) =>
        await Updater.CheckAndInstallAsync(this, () => Task.Run(() => server.StopAsync()).Wait(TimeSpan.FromSeconds(10)));

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    // --- Logs ---------------------------------------------------------------------------------------------------------

    private void PumpLog()
    {
        var added = false;

        while (incoming.TryDequeue(out var entry))
        {
            logEntries.Add(entry);
            recent.Add(entry);
            added = true;
        }

        if (!added)
            return;

        while (logEntries.Count > MaxLogEntries)
            logEntries.RemoveAt(0);

        while (recent.Count > 8)
            recent.RemoveAt(0);

        if (!paused && PageLogs.Visibility == Visibility.Visible)
            ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private bool ShowInLog(LogEntry entry)
    {
        if (LevelWarnings.IsChecked == true && entry.Severity < LogSeverity.Warning)
            return false;

        if (LevelErrors.IsChecked == true && entry.Severity < LogSeverity.Error)
            return false;

        if (SourceGame.IsChecked == true && entry.Source != "Game server")
            return false;

        if (SourceWeb.IsChecked == true && entry.Source is not ("Server" or "Web"))
            return false;

        var search = SearchBox.Text.Trim();

        return search.Length == 0
            || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void LogFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready)
            return;

        logView.Refresh();
        ScrollLogToEnd();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        paused = !paused;
        PauseButton.Content = paused ? "Resume" : "Pause";

        if (!paused)
            ScrollLogToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(string.Join(Environment.NewLine, logView.Cast<LogEntry>().Select(entry => entry.ToString())));

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        logEntries.Clear();
        recent.Clear();
    }

    // --- Status -------------------------------------------------------------------------------------------------------

    private void RefreshStatus()
    {
        var running = server.Server;

        // The game changes the list too (create, login, delete); keep the open page current.
        if (ready && PageCharacters.Visibility == Visibility.Visible)
            RefreshCharacters(force: false);
        var green = (Brush)FindResource("SuccessBrush");
        var grey = (Brush)FindResource("MutedBrush");
        var amber = (Brush)FindResource("WarningBrush");

        StartButton.Visibility = running is null ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = running is null ? Visibility.Collapsed : Visibility.Visible;

        if (busy)
        {
            StatusDot.Fill = amber;
            SidebarDot.Fill = amber;
        }

        if (running is null)
        {
            if (!busy)
            {
                StatusDot.Fill = grey;
                SidebarDot.Fill = grey;
                StatusTitle.Text = "Server stopped";
                StatusDetail.Text = "Press Start server, or Test run in RPCS3 to start everything at once.";
                SidebarStatus.Text = "Stopped";
            }

            DnsState.Text = WebState.Text = GameState.Text = "Stopped";
            DnsState.Foreground = WebState.Foreground = GameState.Foreground = grey;
            UptimeText.Text = "—";
            PlayersText.Text = "0";
            PacketsText.Text = "0";
            SavedCharactersText.Text = string.Empty;
            players.Clear();
            NoPlayers.Visibility = Visibility.Visible;

            return;
        }

        var uptime = DateTime.Now - (server.StartedAt ?? DateTime.Now);
        var sessions = running.Gateway?.SessionSnapshot() ?? [];
        var clock = running.Gateway?.Clock ?? TimeSpan.Zero;
        var character = running.Characters.Snapshot();
        var name = $"{character.GetValueOrDefault("FirstName")} {character.GetValueOrDefault("LastName")}".Trim();

        if (!busy)
        {
            StatusDot.Fill = green;
            SidebarDot.Fill = green;
            StatusTitle.Text = "Server running";
            StatusDetail.Text = $"Listening on {settings.Bind}; the PS3 connects to {settings.Advertise}. {(settings.SanctuaryFlow ? "Sanctuary flow on." : "Basic flow.")}";
            SidebarStatus.Text = "Running";
        }

        DnsState.Text = running.DnsPort is { } dns ? $"Running · UDP {dns}" : "Off";
        DnsState.Foreground = running.DnsPort is null ? grey : green;
        WebState.Text = $"Running · TCP {running.HttpPort}";
        WebState.Foreground = green;
        GameState.Text = running.GatewayPort is { } game ? $"Running · UDP {game}" : "Off";
        GameState.Foreground = running.GatewayPort is null ? grey : green;

        UptimeText.Text = uptime.TotalHours >= 1 ? uptime.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : uptime.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        PlayersText.Text = sessions.Count.ToString(CultureInfo.InvariantCulture);
        PacketsText.Text = (running.Gateway?.PacketCount ?? 0).ToString("N0", CultureInfo.InvariantCulture);
        CharacterText.Text = name.Length > 0 ? name : "—";
        SavedCharactersText.Text = running.Characters.Count switch
        {
            0 => "none saved yet",
            1 => "1 saved character",
            var count => $"{count} saved characters",
        };

        players.Clear();

        foreach (var session in sessions)
        {
            players.Add(new PlayerRow(
                session.OnlineId.Length > 0 ? session.OnlineId : "(connecting)",
                name.Length > 0 ? name : "no character yet",
                session.Peer.ToString(),
                Jobs.NameOf(session.ActiveJob),
                session.InWorld ? "In world" : session.LoggedIn ? "Logged in" : "Connecting",
                session.Packets,
                Ago(clock - session.LastPacketAt),
                Ago(clock - session.ConnectedAt)));
        }

        NoPlayers.Visibility = players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s ago" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago" : $"{(int)span.TotalHours}h ago";
    }

    private void SetBusy(bool value, bool testRun = false)
    {
        busy = value;
        StartButton.IsEnabled = StopButton.IsEnabled = !value;
        TestRunButton.IsEnabled = TestRunButton2.IsEnabled = ApplyGameplayButton.IsEnabled = !value;

        if (value)
        {
            StatusTitle.Text = testRun ? "Test run in progress" : "Working…";
            StatusDetail.Text = testRun ? "Play in RPCS3, then close it to save the report." : "Starting or stopping the services.";
            SidebarStatus.Text = testRun ? "Test run" : "Working";
        }

        RefreshStatus();
    }

    // --- Settings -----------------------------------------------------------------------------------------------------

    private void ShowSettings()
    {
        ClientBox.Text = settings.ClientFolder;
        ExtraClientBox.Text = settings.ExtraClientFolder;
        DigestBox.Text = settings.OriginalDigest;
        MatchLocalSwitch.IsChecked = settings.MatchLocal;
        Rpcs3Box.Text = settings.Rpcs3Executable;
        ElfBox.Text = settings.GameElf;
        RestoreScriptsSwitch.IsChecked = settings.RestoreScripts;
        InstallPatchesSwitch.IsChecked = settings.InstallPatches;
        BindBox.Text = settings.Bind;
        AdvertiseBox.Text = settings.Advertise;
        HttpPortBox.Text = settings.HttpPort.ToString(CultureInfo.InvariantCulture);
        GatewayPortBox.Text = settings.GatewayPort.ToString(CultureInfo.InvariantCulture);
        DnsSwitch.IsChecked = settings.Dns;

        SanctuaryFlowSwitch.IsChecked = settings.SanctuaryFlow;
        StarterItemsSwitch.IsChecked = settings.StarterItems;
        WelcomeSwitch.IsChecked = settings.WelcomeScreen;
        GameTimeSwitch.IsChecked = settings.GameTime;
        JobsBox.Text = settings.Jobs;
        SkyBox.Text = settings.Sky;
        SpawnBox.Text = settings.Spawn;
        Clothes7.IsChecked = settings.ClothesPacket == 7;
        Clothes5.IsChecked = settings.ClothesPacket == 5;
        Clothes0.IsChecked = settings.ClothesPacket == 0;
    }

    /// <summary>Takes the values from the Settings and Gameplay pages and saves them; false (with a message) when one is wrong.</summary>
    private bool ReadSettings()
    {
        if (!int.TryParse(HttpPortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var httpPort) || httpPort is < 1 or > 65535)
            return Invalid("The web server port must be a number from 1 to 65535.");

        if (!int.TryParse(GatewayPortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var gatewayPort) || gatewayPort is < 1 or > 65535)
            return Invalid("The game server port must be a number from 1 to 65535.");

        var next = new ManagerSettings
        {
            ClientFolder = ClientBox.Text.Trim(),
            ExtraClientFolder = ExtraClientBox.Text.Trim(),
            OriginalDigest = DigestBox.Text.Trim(),
            MatchLocal = MatchLocalSwitch.IsChecked == true,
            Rpcs3Executable = Rpcs3Box.Text.Trim(),
            GameElf = ElfBox.Text.Trim(),
            RestoreScripts = RestoreScriptsSwitch.IsChecked == true,
            InstallPatches = InstallPatchesSwitch.IsChecked == true,
            Bind = BindBox.Text.Trim(),
            Advertise = AdvertiseBox.Text.Trim(),
            HttpPort = httpPort,
            GatewayPort = gatewayPort,
            Dns = DnsSwitch.IsChecked == true,
            SanctuaryFlow = SanctuaryFlowSwitch.IsChecked == true,
            StarterItems = StarterItemsSwitch.IsChecked == true,
            WelcomeScreen = WelcomeSwitch.IsChecked == true,
            GameTime = GameTimeSwitch.IsChecked == true,
            Jobs = JobsBox.Text.Trim().Length > 0 ? JobsBox.Text.Trim() : "all",
            Sky = SkyBox.Text.Trim(),
            Spawn = SpawnBox.Text.Trim(),
            ClothesPacket = Clothes5.IsChecked == true ? 5 : Clothes0.IsChecked == true ? 0 : 7,
        };

        try
        {
            next.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid($"The settings could not be saved: {exception.Message}");
        }

        settings = next;

        return true;
    }

    private bool Invalid(string message)
    {
        MessageBox.Show(this, message, "Check the settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        NavSettings.IsChecked = true;

        return false;
    }

    private void BrowseClient_Click(object sender, RoutedEventArgs e) => PickFolder(ClientBox, "Choose your NPUA30048 client folder");

    private void BrowseExtra_Click(object sender, RoutedEventArgs e) => PickFolder(ExtraClientBox, "Choose a second NPUA30048 folder");

    private void BrowseDigest_Click(object sender, RoutedEventArgs e) => _ = PickFile(DigestBox, "Patch digest (*.soe)|*.soe|All files|*.*");

    private void BrowseRpcs3_Click(object sender, RoutedEventArgs e)
    {
        if (!PickFile(Rpcs3Box, "RPCS3 (rpcs3.exe)|rpcs3.exe|Programs (*.exe)|*.exe"))
            return;

        // Fill in the game RPCS3 has installed, where nothing is chosen yet.
        var (client, executable) = ManagerSettings.GameInRpcs3(Rpcs3Box.Text);

        if (ElfBox.Text.Trim().Length == 0 && executable is not null)
            ElfBox.Text = executable;

        if (ClientBox.Text.Trim().Length == 0 && client is not null)
            ClientBox.Text = client;
    }

    private void BrowseElf_Click(object sender, RoutedEventArgs e) => _ = PickFile(ElfBox, "Game (game.elf, EBOOT.BIN)|*.elf;*.BIN|All files|*.*");

    private void PickFolder(TextBox box, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };

        if (Directory.Exists(box.Text))
            dialog.InitialDirectory = box.Text;

        if (dialog.ShowDialog(this) == true)
            box.Text = dialog.FolderName;
    }

    private bool PickFile(TextBox box, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };

        if (File.Exists(box.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(box.Text);

        if (dialog.ShowDialog(this) != true)
            return false;

        box.Text = dialog.FileName;

        return true;
    }

    // --- Closing ------------------------------------------------------------------------------------------------------

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing || !server.IsRunning)
            return;

        e.Cancel = true;
        closing = true;

        await server.StopAsync();
        Close();
    }
}
