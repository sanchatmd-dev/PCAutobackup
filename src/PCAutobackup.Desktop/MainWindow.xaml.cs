using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCAutobackup.Core;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Desktop;

public partial class MainWindow : Window
{
    private readonly ApplicationService _service;
    private readonly IdleTransferMemoryCleanup _idleMemoryCleanup = new();
    private SourceRemovalUndoHistory SourceUndoHistory => new(_service.Configuration);
    private System.Windows.Threading.DispatcherTimer? _sourceUndoTimer;
    private CancellationTokenSource? _jobCancellation;
    private bool _acceptProgress;
    private readonly Stopwatch _jobElapsed = new();
    private bool _transferCompleted;
    private bool _loaded;
    private bool _populating;
    private bool _closeAfterJob;
    private string _currentPage = "Overview";
    private string? _displayLanguage;

    public MainWindow(string? configurationPath = null)
    {
        _service = new ApplicationService(configurationPath);
        InitializeComponent();
        ApplyLanguage();
        Navigate("Overview");
    }

    private string T(string text) => AppLocalization.Text(text, _service.Configuration.Language);
    private string F(string text, params object?[] arguments) => AppLocalization.Format(text, _service.Configuration.Language, arguments);
    private string Diagnostic(string text) => AppLocalization.TranslateMessage(text, _service.Configuration.Language);

    private void ApplyLanguage()
    {
        var language = AppLocalization.NormalizeLanguage(_service.Configuration.Language);
        if (_displayLanguage == language) return;
        _displayLanguage = language;
        foreach (var text in AppLocalization.English.Keys)
            Resources[LocalizeExtension.ResourceKey(text)] = T(text);
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(AppLocalization.CultureFor(language).Name);
        Navigate(_currentPage);
    }

    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _populating || LanguageBox.SelectedValue is not string language || language == _service.Configuration.Language) return;
        // Language changes persist independently; unsaved schedule/retention edits remain on screen.
        var dailyTimes = DailyTimeBox.Text;
        var retention = RetentionBox.Text;
        var quota = QuotaBox.Text;
        var scheduleEnabled = ScheduleEnabledBox.IsChecked;
        var inventoryEnabled = InventoryBox.IsChecked;
        var clientId = ClientIdBox.Text;
        var clientSecret = ClientSecretBox.Password;
        var repositoryText = RepositoryText.Text;
        var snapshots = (SnapshotsList.ItemsSource as IEnumerable<SnapshotRow>)?.ToList();
        var selectedSnapshot = (SnapshotsList.SelectedItem as SnapshotRow)?.Id;
        await RunJobAsync(T("กำลังบันทึกการตั้งค่า…"), async ct =>
        {
            _service.Configuration.Language = AppLocalization.NormalizeLanguage(language);
            await _service.SaveConfigurationAsync();
            PopulateConfiguration();
            _populating = true;
            try
            {
                DailyTimeBox.Text = dailyTimes;
                RetentionBox.Text = retention;
                QuotaBox.Text = quota;
                ScheduleEnabledBox.IsChecked = scheduleEnabled;
                InventoryBox.IsChecked = inventoryEnabled;
                ClientIdBox.Text = clientId;
                ClientSecretBox.Password = clientSecret;
            }
            finally { _populating = false; }
            RepositoryText.Text = Diagnostic(repositoryText);
            if (snapshots is not null)
            {
                var rows = snapshots.Select(row => new SnapshotRow(row.Snapshot, language)).ToList();
                SnapshotsList.ItemsSource = rows;
                SnapshotsList.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedSnapshot);
                EmptyHistory.Text = rows.Count == 0 ? T("ยังไม่พบชุดสำรองที่เสร็จสมบูรณ์ในคลังนี้") : F("พบ {0:N0} ชุดสำรอง", rows.Count);
            }
            StatusText.Text = T("เปลี่ยนภาษาแล้ว");
        });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังอ่านการตั้งค่า…"), async ct =>
        {
            await _service.LoadConfigurationAsync();
            _loaded = true;
            PopulateConfiguration();
            await RefreshRunHistoryAsync();
            StatusText.Text = T("พร้อมใช้งาน · ปิดหรือย่อหน้าต่างเพื่อทำงานต่อในถาดระบบ");
        });
    }

    private async Task RefreshRunHistoryAsync()
    {
        var skipped = await _service.GetLastScheduledSkipAsync();
        ScheduledSkipText.Text = skipped is null ? "" : F("รอบอัตโนมัติที่ข้ามล่าสุด · {0} · {1}", T("ข้ามเพราะมีงานกำลังทำอยู่"), LocalDate(skipped.StartedUtc));
        ScheduledSkipText.Visibility = skipped is null ? Visibility.Collapsed : Visibility.Visible;
        var success = await _service.GetLastSuccessAsync();
        LastBackupText.Text = success is null ? T("ยังไม่มีข้อมูลการสำรองสำเร็จ") : F("สำรองสำเร็จล่าสุด {0} · {1:N0} ไฟล์ · {2}", LocalDate(success.CompletedUtc ?? success.StartedUtc), success.FilesProcessed, FormatBytes(success.BytesProcessed));
        var latest = await _service.GetLastRunAsync();
        if (latest is null)
        {
            LatestRunText.Text = T("ยังไม่มีงานสำรองที่บันทึกไว้");
            LatestRunText.Foreground = (Brush)FindResource("Muted");
            return;
        }
        var state = latest.Status switch
        {
            "Succeeded" => T("สำเร็จ"),
            "Failed" => T("ไม่สำเร็จ"),
            "Cancelled" => T("ยกเลิก"),
            "Interrupted" => T("งานถูกขัดจังหวะ · จะลองใหม่เมื่อเปิดสำรองอัตโนมัติ"),
            "Running" => T("เริ่มทำงานแล้ว (สถานะที่บันทึกล่าสุด)"),
            _ => latest.Status
        };
        LatestRunText.Text = F("งานล่าสุด · {0} · {1}", state, LocalDate(latest.CompletedUtc ?? latest.StartedUtc)) + (latest.Status == "Failed" && !string.IsNullOrWhiteSpace(latest.Message) ? $"\n{Diagnostic(latest.Message)}" : "");
        LatestRunText.Foreground = latest.Status == "Failed" ? new SolidColorBrush(Color.FromRgb(155, 66, 47)) : (Brush)FindResource("Muted");
    }

    private string LocalDate(DateTimeOffset value) => value.ToLocalTime().ToString("dd MMM yyyy HH:mm", AppLocalization.CultureFor(_service.Configuration.Language));

    private async void ReloadConfiguration_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังอ่านการตั้งค่าที่บันทึกไว้…"), async ct =>
        {
            await _service.LoadConfigurationAsync();
            _loaded = true;
            PopulateConfiguration();
            ClearSnapshotList();
            await RefreshRunHistoryAsync();
            StatusText.Text = T("อ่านการตั้งค่าและสถานะที่บันทึกไว้แล้ว");
        });
    }

    private void PopulateConfiguration()
    {
        _populating = true;
        var config = _service.Configuration;
        ApplyLanguage();
        LanguageBox.SelectedValue = AppLocalization.NormalizeLanguage(config.Language);
        DeviceText.Text = config.DeviceName;
        GoogleRadio.IsChecked = config.Destination == DestinationKind.GoogleDrive;
        ExternalRadio.IsChecked = config.Destination == DestinationKind.ExternalDrive;
        LocalRadio.IsChecked = config.Destination == DestinationKind.LocalFolder;
        LocalDestinationText.Text = string.IsNullOrWhiteSpace(config.LocalDestinationPath) ? T("ยังไม่ได้เลือกโฟลเดอร์") : config.LocalDestinationPath;
        GoogleAccountText.Text = string.IsNullOrWhiteSpace(config.GoogleEmail) ? T("ยังไม่ได้เชื่อมต่อบัญชี") : F("เชื่อมต่อแล้ว · {0}", config.GoogleEmail);
        DisconnectButton.IsEnabled = !string.IsNullOrWhiteSpace(config.ProtectedGoogleTokens);
        ClientIdBox.Text = config.GoogleClientId;
        ClientSecretBox.Password = config.GoogleClientSecret;
        ScheduleEnabledBox.IsChecked = config.ScheduleEnabled;
        var times = BackupSchedule.GetTimes(config);
        DailyTimeBox.Text = string.Join(", ", times);
        RetentionBox.Text = config.RetentionDays.ToString(CultureInfo.InvariantCulture);
        QuotaBox.Text = config.QuotaBytes is { } quota ? (quota / 1_000_000_000m).ToString("0.#########", CultureInfo.InvariantCulture) : "";
        InventoryBox.IsChecked = config.IncludeApplicationInventory;
        SourceCountText.Text = config.Sources.Count.ToString("N0");
        SourcesList.ItemsSource = null;
        SourcesList.ItemsSource = config.Sources;
        EmptySources.Visibility = config.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceUndoNotice();
        ScheduleText.Text = !config.ScheduleEnabled ? T("ยังไม่เปิด") : times.Count == 1 ? times[0] : F("{0} รอบ / วัน", times.Count);
        ScheduleCaption.Text = config.ScheduleEnabled ? string.Join(", ", times) + T(" · เวลาของ Windows") : T("ตั้งเวลาได้ในการตั้งค่า");
        RetentionText.Text = config.RetentionDays == 0 ? T("ชุดล่าสุด") : F("{0:N0} วัน", config.RetentionDays);
        DestinationSummary.Text = config.Destination == DestinationKind.GoogleDrive
            ? "Google Drive · " + (config.GoogleEmail ?? T("ยังไม่ได้เชื่อมต่อ"))
            : (config.Destination == DestinationKind.ExternalDrive ? T("HDD / SSD ภายนอก") : T("โฟลเดอร์ในเครื่อง")) + " · " + (string.IsNullOrWhiteSpace(config.LocalDestinationPath) ? T("ยังไม่ได้เลือก") : config.LocalDestinationPath);
        var hasKey = !string.IsNullOrWhiteSpace(config.ProtectedRecoveryKey);
        KeyStateText.Text = config.RecoveryKeyAcknowledged && hasKey ? T("มีกุญแจในเครื่อง และเคยส่งออกหรือนำเข้าแล้ว") : T("ยังต้องส่งออกกุญแจกู้คืน ก่อนเริ่มสำรอง");
        KeySummary.Text = config.RecoveryKeyAcknowledged && hasKey ? T("เก็บสำเนากุญแจไว้นอกเครื่องนี้เสมอ เพื่อใช้เมื่อเครื่องเดิมเข้าไม่ได้") : T("เก็บไฟล์กุญแจไว้นอกเครื่องนี้ เพื่อกู้คืนได้เมื่อเปลี่ยน PC หรือ Windows เสีย");
        var destinationReady = config.Destination == DestinationKind.GoogleDrive
            ? !string.IsNullOrWhiteSpace(config.ProtectedGoogleTokens)
            : !string.IsNullOrWhiteSpace(config.LocalDestinationPath);
        var ready = config.Sources.Count > 0 && destinationReady && hasKey && config.RecoveryKeyAcknowledged;
        ReadinessTitle.Text = ready ? T("พร้อมสำรองเมื่อคุณต้องการ") : T("เริ่มต้นปกป้องไฟล์ของคุณ");
        ReadinessText.Text = ready ? T("สำรองไฟล์ที่เปลี่ยนแปลง และเก็บชุดที่กู้คืนได้ตามระยะเวลาที่ตั้งไว้") : T("เลือกโฟลเดอร์ พื้นที่จัดเก็บ และเก็บกุญแจกู้คืนก่อนสำรองครั้งแรก");
        UpdateDestinationPanels();
        _populating = false;
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate((string)((Button)sender).Tag);

    private void Navigate(string page)
    {
        _currentPage = page;
        var pages = new Dictionary<string, (StackPanel Panel, Button Nav, string Title, string Subtitle)>
        {
            ["Overview"] = (OverviewPage, OverviewNav, T("ภาพรวม"), T("ดูแลไฟล์สำคัญของคุณ ในจังหวะที่ตั้งไว้")),
            ["Sources"] = (SourcesPage, SourcesNav, T("โฟลเดอร์ต้นทาง"), T("เลือกไฟล์สำคัญ โดยไม่ต้องสำรองทั้งเครื่อง")),
            ["Destination"] = (DestinationPage, DestinationNav, T("พื้นที่จัดเก็บ"), T("ให้ไฟล์มีบ้านอีกแห่งที่คุณเข้าถึงได้")),
            ["History"] = (HistoryPage, HistoryNav, T("ประวัติและกู้คืน"), T("ย้อนกลับไปยังชุดสำรองที่เสร็จสมบูรณ์")),
            ["Settings"] = (SettingsPage, SettingsNav, T("การตั้งค่า"), T("กำหนดเวลา ระยะเวลาเก็บ และดูแลกุญแจกู้คืน"))
        };
        foreach (var (key, item) in pages)
        {
            var selected = key == page;
            item.Panel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            item.Nav.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#315D56" : "#173E3C"));
            item.Nav.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#FFFFFF" : "#BDCEC8"));
            item.Nav.BorderBrush = Brushes.Transparent;
            item.Nav.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        PageTitle.Text = pages[page].Title;
        PageSubtitle.Text = pages[page].Subtitle;
        MainContent.ScrollToTop();
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("เลือกโฟลเดอร์ต้นทาง"), Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync(T("กำลังบันทึกโฟลเดอร์…"), async ct =>
        {
            foreach (var path in dialog.FolderNames)
            {
                if (_service.Configuration.Sources.Any(s => string.Equals(Path.TrimEndingDirectorySeparator(s.Path), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))) continue;
                _service.Configuration.Sources.Add(new BackupSource { Path = path, Label = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path });
            }
            await _service.SaveConfigurationAsync();
            PopulateConfiguration();
            StatusText.Text = T("บันทึกโฟลเดอร์ต้นทางแล้ว");
        });
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        await RunJobAsync(T("กำลังบันทึกโฟลเดอร์…"), async ct =>
        {
            if (!await SourceUndoHistory.RemoveAsync(id, _service.SaveConfigurationAsync, DateTimeOffset.UtcNow)) return;
            PopulateConfiguration();
            StatusText.Text = T("นำโฟลเดอร์ออกจากการสำรองแล้ว · ไฟล์ต้นฉบับยังอยู่ตามเดิม");
        });
    }

    private async void UndoRemoveSource_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังคืนโฟลเดอร์…"), async ct =>
        {
            if (!await SourceUndoHistory.UndoLatestAsync(_service.SaveConfigurationAsync))
            {
                UpdateSourceUndoNotice();
                StatusText.Text = T("เลิกทำไม่ได้ เพราะมีโฟลเดอร์หรือรหัสเดียวกันอยู่แล้ว");
                return;
            }
            PopulateConfiguration();
            StatusText.Text = T("คืนโฟลเดอร์เข้าสู่รายการสำรองแล้ว");
        });
    }

    private void UpdateSourceUndoNotice()
    {
        _sourceUndoTimer?.Stop();
        var history = SourceUndoHistory;
        SourceHistoryPanel.Visibility = history.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SourceHistoryText.Text = F("มีโฟลเดอร์ที่นำออกและยังไม่ยืนยัน {0:N0} รายการ", history.Count);
        var entry = history.Peek();
        var remaining = history.BannerRemaining(DateTimeOffset.UtcNow);
        SourceUndoPanel.Visibility = remaining > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        if (entry is null || remaining <= TimeSpan.Zero) return;
        var canRestore = history.CanRestore(_service.Configuration.Sources);
        UndoSourceButton.IsEnabled = canRestore;
        SourceUndoNotice.Text = F("นำโฟลเดอร์ออกแล้ว: {0}", entry.Source.Path)
            + (canRestore ? "" : " · " + T("เลิกทำไม่ได้ เพราะมีโฟลเดอร์หรือรหัสเดียวกันอยู่แล้ว"));
        if (_sourceUndoTimer is null)
        {
            _sourceUndoTimer = new System.Windows.Threading.DispatcherTimer();
            _sourceUndoTimer.Tick += SourceUndoTimer_Tick;
        }
        _sourceUndoTimer.Interval = remaining;
        _sourceUndoTimer.Start();
    }

    private void SourceUndoTimer_Tick(object? sender, EventArgs e)
    {
        _sourceUndoTimer?.Stop();
        UpdateSourceUndoNotice();
    }

    private async void ConfirmSourceRemoval_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังบันทึกโฟลเดอร์…"), async ct =>
        {
            await SourceUndoHistory.ConfirmLatestAsync(_service.SaveConfigurationAsync);
            PopulateConfiguration();
            StatusText.Text = T("ยืนยันการนำโฟลเดอร์ออกแล้ว · ไฟล์ต้นฉบับยังอยู่ตามเดิม");
        });
    }

    private async void RestoreRemovedSources_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังคืนโฟลเดอร์…"), async ct =>
        {
            var result = await SourceUndoHistory.RestoreAllAsync(_service.SaveConfigurationAsync);
            PopulateConfiguration();
            StatusText.Text = F("คืนโฟลเดอร์แล้ว {0:N0} รายการ · ยังมีรายการซ้ำที่ไม่ได้คืน {1:N0} รายการ", result.RestoredCount, result.ConflictCount);
        });
    }

    private async void ClearSourceRemovalHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_jobCancellation is not null || SourceUndoHistory.Count == 0) return;
        var confirmed = MessageBox.Show(this,
            T("ล้างประวัติการนำโฟลเดอร์ออกทั้งหมดหรือไม่?\n\nรายการที่นำออกจะยังไม่ถูกสำรอง และจะเลิกทำจากประวัตินี้ไม่ได้ ไฟล์ต้นฉบับและรายการโฟลเดอร์ที่ยังใช้งานจะไม่ถูกลบ"),
            T("ล้างประวัติ"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return;
        await RunJobAsync(T("กำลังบันทึกโฟลเดอร์…"), async ct =>
        {
            await SourceUndoHistory.ClearAsync(confirmed, _service.SaveConfigurationAsync);
            PopulateConfiguration();
            StatusText.Text = T("ล้างประวัติการนำโฟลเดอร์ออกแล้ว");
        });
    }

    private void UpdateDestinationPanels()
    {
        if (GooglePanel is null || LocalPanel is null) return;
        GooglePanel.Visibility = GoogleRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LocalPanel.Visibility = GoogleRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Destination_Checked(object sender, RoutedEventArgs e)
    {
        UpdateDestinationPanels();
        if (!_loaded || _populating) return;
        await RunJobAsync(T("กำลังเปลี่ยนพื้นที่จัดเก็บ…"), async ct =>
        {
            _service.Configuration.Destination = GoogleRadio.IsChecked == true ? DestinationKind.GoogleDrive : ExternalRadio.IsChecked == true ? DestinationKind.ExternalDrive : DestinationKind.LocalFolder;
            await _service.SaveConfigurationAsync();
            PopulateConfiguration();
            ClearSnapshotList();
            StatusText.Text = T("บันทึกประเภทพื้นที่จัดเก็บแล้ว");
        });
    }

    private async void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("เลือกโฟลเดอร์เฉพาะสำหรับคลังสำรอง") };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync(T("กำลังบันทึกพื้นที่จัดเก็บ…"), async ct =>
        {
            _service.Configuration.LocalDestinationPath = dialog.FolderName;
            _service.Configuration.ExternalVolumeId = null;
            await _service.SaveConfigurationAsync();
            PopulateConfiguration();
            ClearSnapshotList();
            StatusText.Text = T("บันทึกพื้นที่จัดเก็บแล้ว · ยังไม่ได้เริ่มสำรอง");
        });
    }

    private async void ImportClient_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = T("นำเข้า Google Desktop OAuth client"), Filter = "OAuth client JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync(T("กำลังนำเข้าการตั้งค่า OAuth…"), async ct =>
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 1024 * 1024) throw new InvalidOperationException(T("ไฟล์ OAuth client มีขนาดใหญ่เกินไป"));
            await _service.ImportGoogleClientAsync(await File.ReadAllTextAsync(dialog.FileName, ct));
            PopulateConfiguration();
            StatusText.Text = T("นำเข้า OAuth client แล้ว · กดเชื่อมต่อ Google เพื่ออนุญาตบัญชี");
        });
    }

    private async void SaveClient_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังบันทึก OAuth client…"), async ct =>
        {
            if (string.IsNullOrWhiteSpace(ClientIdBox.Text)) throw new InvalidOperationException(T("กรุณาระบุ Client ID ของ Google Desktop app"));
            var json = System.Text.Json.JsonSerializer.Serialize(new { installed = new { client_id = ClientIdBox.Text.Trim(), client_secret = ClientSecretBox.Password } });
            await _service.ImportGoogleClientAsync(json);
            PopulateConfiguration();
            StatusText.Text = T("บันทึก OAuth client แล้ว");
        });
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังรอการอนุญาตจาก Google ในเบราว์เซอร์…"), async ct =>
        {
            await _service.SignInGoogleAsync(string.IsNullOrWhiteSpace(EmailHintBox.Text) ? null : EmailHintBox.Text.Trim(), ct);
            PopulateConfiguration();
            StatusText.Text = F("Google ยืนยันบัญชี {0} แล้ว", _service.Configuration.GoogleEmail);
        });
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังยกเลิกการเชื่อมต่อ…"), async ct =>
        {
            await _service.DisconnectGoogleAsync();
            PopulateConfiguration();
            ClearSnapshotList();
            StatusText.Text = T("ยกเลิกการเชื่อมต่อ Google ในเครื่องนี้แล้ว");
        });
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังบันทึกการตั้งค่า…"), async ct =>
        {
            var times = BackupSchedule.ParseTimes(DailyTimeBox.Text.Trim());
            if (!int.TryParse(RetentionBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 0) throw new InvalidOperationException(T("จำนวนวันต้องเป็นจำนวนเต็มตั้งแต่ 0 ขึ้นไป"));
            long? quota = null;
            if (!string.IsNullOrWhiteSpace(QuotaBox.Text))
            {
                if (!decimal.TryParse(QuotaBox.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var gb) || gb <= 0 || gb > long.MaxValue / 1_000_000_000m) throw new InvalidOperationException(T("โควตาต้องมากกว่า 0 GB หรือเว้นว่างเพื่อไม่จำกัด"));
                quota = checked((long)(gb * 1_000_000_000m));
                if (quota == 0) throw new InvalidOperationException(T("โควตามีขนาดเล็กเกินไป"));
            }
            _service.Configuration.DailyTimes = times;
            _service.Configuration.DailyTime = times[0];
            _service.Configuration.RetentionDays = days;
            _service.Configuration.QuotaBytes = quota;
            await _service.SetScheduleAsync(ScheduleEnabledBox.IsChecked == true);
            PopulateConfiguration();
            StatusText.Text = T("บันทึกการตั้งค่าและตารางเวลาของ Windows แล้ว");
        });
    }

    private async void Inventory_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _populating) return;
        await RunJobAsync(T("กำลังบันทึกการตั้งค่า…"), async ct =>
        {
            _service.Configuration.IncludeApplicationInventory = InventoryBox.IsChecked == true;
            await _service.SaveConfigurationAsync();
            StatusText.Text = T("บันทึกการแนบรายชื่อแอปแล้ว");
        });
    }

    private async void ExportKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = T("ส่งออกกุญแจ — เก็บสำเนาไว้นอกเครื่องนี้"), Filter = "Recovery key (*.pcakey)|*.pcakey", FileName = "PCAutobackup-recovery.pcakey", AddExtension = true, DefaultExt = ".pcakey" };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync(T("กำลังส่งออกกุญแจกู้คืน…"), async ct =>
        {
            await _service.ExportRecoveryKeyAsync(dialog.FileName);
            PopulateConfiguration();
            StatusText.Text = T("ส่งออกกุญแจแล้ว · เก็บสำเนาไว้นอกเครื่องนี้ในที่ปลอดภัย");
            MessageBox.Show(this, T("ส่งออกกุญแจกู้คืนแล้ว\n\nเก็บสำเนาไว้นอก PC นี้ เช่น USB ที่ปลอดภัย และอย่าส่งให้ผู้อื่น หากกุญแจหาย จะไม่สามารถถอดรหัสคลังสำรองได้"), T("เก็บกุญแจกู้คืนให้ปลอดภัย"), MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async void ImportKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = T("เลือกกุญแจกู้คืนจากเครื่องเดิม"), Filter = T("Recovery key (*.pcakey;*.txt)|*.pcakey;*.txt|ทุกไฟล์ (*.*)|*.*") };
        if (dialog.ShowDialog(this) != true) return;
        var hasBackupKey = !string.IsNullOrWhiteSpace(_service.Configuration.ProtectedRecoveryKey);
        await RunJobAsync(T("กำลังนำเข้ากุญแจกู้คืน…"), async ct =>
        {
            await _service.ImportRecoveryKeyAsync(dialog.FileName);
            PopulateConfiguration();
            ClearSnapshotList();
            StatusText.Text = hasBackupKey
                ? T("นำเข้ากุญแจสำหรับกู้คืนในครั้งนี้แล้ว · กุญแจสำรองอัตโนมัติเดิมยังคงอยู่")
                : T("นำเข้ากุญแจแล้ว · เปิดคลังสำรองเดิมเพื่ออ่านประวัติ");
        });
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังเตรียมสำรองข้อมูล…"), async ct =>
        {
            var result = await _service.BackupAsync(CreateProgress(), ct);
            CompleteTransferProgress(result.TotalBytes);
            LastBackupText.Text = F("สำรองสำเร็จล่าสุด {0} · {1:N0} ไฟล์ · {2}", LocalDate(DateTimeOffset.Now), result.FileCount, FormatBytes(result.TotalBytes));
            StatusText.Text = F("สำรองสำเร็จ · {0:N0} ไฟล์ · ส่งข้อมูล {1}", result.FileCount, FormatBytes(result.UploadedBytes));
            ShowWarnings(result.Warnings);
        }, releaseTransferMemory: true);
    }

    private async void RefreshSnapshots_Click(object sender, RoutedEventArgs e) => await RunJobAsync(T("กำลังอ่านคลังสำรอง…"), ct => LoadSnapshotsAsync(ct));

    private async Task LoadSnapshotsAsync(CancellationToken ct)
    {
        var snapshots = await _service.ListSnapshotsAsync(ct);
        SnapshotsList.ItemsSource = snapshots.OrderByDescending(s => s.CreatedUtc).Select(s => new SnapshotRow(s, _service.Configuration.Language)).ToList();
        // Rows marked damaged are listed so the user sees them, but they are not restorable snapshots.
        int usable = snapshots.Count(s => s.DeviceName != BackupEngine.DamagedSnapshotLabel);
        EmptyHistory.Text = usable == 0 ? T("ยังไม่พบชุดสำรองที่เสร็จสมบูรณ์ในคลังนี้") : F("พบ {0:N0} ชุดสำรอง", usable);
        StatusText.Text = F("อ่านประวัติสำรองแล้ว · {0:N0} ชุด", usable);
    }

    private void ClearSnapshotList()
    {
        SnapshotsList.ItemsSource = null;
        EmptyHistory.Text = T("กดรีเฟรชเพื่ออ่านชุดสำรองจากคลังที่เลือก");
    }

    private async void OpenLocalRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("เลือกโฟลเดอร์คลังสำรองเดิม (ไม่ใช่โฟลเดอร์ที่จะรับไฟล์)") };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync(T("กำลังเปิดคลังสำรองเดิม…"), async ct =>
        {
            ClearSnapshotList();
            await _service.SelectLocalRepositoryAsync(dialog.FolderName, ct);
            RepositoryText.Text = T("คลังกู้คืน · ") + dialog.FolderName;
            await LoadSnapshotsAsync(ct);
        });
    }

    private async void FindGoogleRepositories_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync(T("กำลังค้นหาคลังสำรองใน Google Drive…"), async ct =>
        {
            var repositories = await _service.ListGoogleRepositoriesAsync(ct);
            GoogleRepositoriesBox.DisplayMemberPath = nameof(GoogleRepository.DeviceName);
            GoogleRepositoriesBox.ItemsSource = repositories;
            GoogleRepositoriesBox.Visibility = Visibility.Visible;
            StatusText.Text = repositories.Count == 0 ? T("ไม่พบคลังที่แอปมีสิทธิ์เข้าถึงในบัญชีนี้") : F("พบ {0:N0} คลัง · เลือกเครื่องจากรายการ", repositories.Count);
        });
    }

    private async void GoogleRepository_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GoogleRepositoriesBox.SelectedItem is not GoogleRepository repository || _jobCancellation is not null) return;
        await RunJobAsync(T("กำลังเปิดคลังสำรอง Google Drive…"), async ct =>
        {
            ClearSnapshotList();
            await _service.SelectGoogleRepositoryAsync(repository.Id, ct);
            RepositoryText.Text = T("คลังกู้คืน Google Drive · ") + repository.DeviceName;
            await LoadSnapshotsAsync(ct);
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotRow row)
        {
            MessageBox.Show(this, T("เลือกชุดสำรองจากรายการก่อนกู้คืน"), T("เลือกชุดสำรอง"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFolderDialog { Title = T("เลือกโฟลเดอร์ว่างสำหรับรับไฟล์ที่กู้คืน") };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync(T("กำลังตรวจสอบและกู้คืนไฟล์…"), async ct =>
        {
            if (Directory.EnumerateFileSystemEntries(dialog.FolderName).Any()) throw new InvalidOperationException(T("กรุณาเลือกโฟลเดอร์ว่าง เพื่อแยกไฟล์ที่กู้คืนจากไฟล์เดิมอย่างชัดเจน"));
            var result = await _service.RestoreAsync(row.Id, dialog.FolderName, CreateProgress(), ct);
            CompleteTransferProgress(result.BytesRestored);
            // Each warning is one file that could not be restored; never present that as a complete restore.
            StatusText.Text = result.Warnings.Count == 0
                ? F("กู้คืนสำเร็จ · {0:N0} ไฟล์ · {1}", result.FilesRestored, FormatBytes(result.BytesRestored))
                : F("กู้คืนได้บางส่วน · {0:N0} ไฟล์ · {1} · กู้คืนไม่ได้ {2:N0} ไฟล์ · รายการทั้งหมดอยู่ใน {3}", result.FilesRestored, FormatBytes(result.BytesRestored), result.Warnings.Count, ApplicationService.UnrestoredReportName);
            if (result.Warnings.Count > 0 && _closeAfterJob) ShowSettings();
            ShowWarnings(result.Warnings);
        }, releaseTransferMemory: true);
    }

    private IProgress<BackupProgress> CreateProgress()
    {
        var job = _jobCancellation;
        UpdateTransferMetrics(0, null);
        return new ThrottledProgress(action => Dispatcher.BeginInvoke(action), progress =>
        {
            // Queued progress must not replace a terminal result or update a later job.
            if (!_acceptProgress || job is null || !ReferenceEquals(_jobCancellation, job)) return;
            StatusText.Text = TranslateStage(progress.Stage);
            ProgressDetailText.Text = F("{0:N0} ไฟล์ · {1}", progress.FilesProcessed, FormatBytes(progress.BytesProcessed)) + (string.IsNullOrEmpty(progress.CurrentFile) ? "" : " · " + progress.CurrentFile);
            ProgressDetailText.ToolTip = ProgressDetailText.Text;
            UpdateTransferMetrics(progress.BytesProcessed, progress.TotalBytes);
        });
    }

    private void UpdateTransferMetrics(long processedBytes, long? totalBytes, bool completed = false)
    {
        var percent = AppLocalization.ProgressPercentage(processedBytes, totalBytes, completed);
        JobProgress.IsIndeterminate = percent is null;
        JobProgress.Value = percent ?? 0;
        ProgressMetricsText.Text = AppLocalization.FormatTransferMetrics(processedBytes, totalBytes, _jobElapsed.Elapsed, _service.Configuration.Language, completed);
        ProgressMetricsText.Visibility = Visibility.Visible;
    }

    private void CompleteTransferProgress(long processedBytes)
    {
        _acceptProgress = false;
        _transferCompleted = true;
        _jobElapsed.Stop();
        UpdateTransferMetrics(processedBytes, processedBytes, completed: true);
    }

    private string TranslateStage(string stage) => stage.ToLowerInvariant() switch
    {
        "scanning" or "scan" => T("กำลังอ่านรายการไฟล์…"),
        "backingup" or "backup" or "uploading" => T("กำลังเข้ารหัสและสำรองไฟล์…"),
        "committing" or "commit" => T("กำลังยืนยันชุดสำรอง…"),
        "pruning" or "retention" => T("กำลังจัดเก็บประวัติตามการตั้งค่า…"),
        "restoring" or "restore" => T("กำลังตรวจสอบและกู้คืนไฟล์…"),
        "finalizing" => AppLocalization.NormalizeLanguage(_service.Configuration.Language) == "en" ? "Finalizing files…" : "กำลังตรวจสอบขั้นสุดท้าย…",
        _ => T("กำลังทำงาน · ") + stage
    };

    private async Task RunJobAsync(string initialStatus, Func<CancellationToken, Task> work, bool releaseTransferMemory = false)
    {
        if (_jobCancellation is not null) return;
        _jobCancellation = new CancellationTokenSource();
        _acceptProgress = true;
        _transferCompleted = false;
        ReloadConfigurationButton.IsEnabled = false;
        MainContent.IsEnabled = false;
        JobProgress.Visibility = Visibility.Visible;
        JobProgress.IsIndeterminate = true;
        JobProgress.Value = 0;
        ProgressMetricsText.Text = "";
        ProgressMetricsText.Visibility = Visibility.Collapsed;
        ProgressDetailText.Visibility = Visibility.Visible;
        ProgressDetailText.Text = "";
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        StatusText.Text = initialStatus;
        try
        {
            await _idleMemoryCleanup.BeginJobAsync();
            _jobElapsed.Restart();
            await work(_jobCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _acceptProgress = false;
            StatusText.Text = T("ยกเลิกงานแล้ว · ชุดสำรองที่สำเร็จก่อนหน้ายังคงอยู่");
        }
        catch (Exception ex)
        {
            _acceptProgress = false;
            var message = Diagnostic(ex.Message);
            try
            {
                await _service.LoadConfigurationAsync();
                _loaded = true;
                PopulateConfiguration();
            }
            catch
            {
                _loaded = false;
                ReadinessTitle.Text = T("ยังอ่านการตั้งค่าที่บันทึกไว้ไม่ได้");
                ReadinessText.Text = T("ตรวจสอบไฟล์การตั้งค่า แล้วกดรีเฟรชการตั้งค่าอีกครั้ง");
                message += T("\n\nอ่านการตั้งค่าที่บันทึกไว้ไม่ได้ กรุณาตรวจสอบแล้วกดรีเฟรชการตั้งค่า");
            }
            StatusText.Text = T("ทำงานไม่สำเร็จ · ") + Diagnostic(ex.Message);
            // A job that fails after its window was closed must not exit silently: a failed restore leaves a partial folder and no status record.
            if (_closeAfterJob) ShowSettings();
            if (IsVisible) MessageBox.Show(this, message, T("ทำงานไม่สำเร็จ"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _acceptProgress = false;
            _jobElapsed.Stop();
            // The result is final; a late Cancel click during the refresh below must not replace it with "cancelling".
            CancelButton.IsEnabled = false;
            try { if (_loaded) await RefreshRunHistoryAsync(); }
            catch { LatestRunText.Text = T("อ่านสถานะงานล่าสุดไม่ได้ · กดรีเฟรชการตั้งค่าเพื่อลองอีกครั้ง"); }
            _jobCancellation.Dispose();
            _jobCancellation = null;
            ReloadConfigurationButton.IsEnabled = true;
            MainContent.IsEnabled = _loaded;
            JobProgress.Visibility = _transferCompleted ? Visibility.Visible : Visibility.Collapsed;
            ProgressMetricsText.Visibility = _transferCompleted ? Visibility.Visible : Visibility.Collapsed;
            ProgressDetailText.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            var completedGeneration = _idleMemoryCleanup.CompleteJob();
            if (releaseTransferMemory && !_closeAfterJob) QueueTransferMemoryCleanup(completedGeneration);
            if (_closeAfterJob) Close();
        }
    }

    private void QueueTransferMemoryCleanup(long completedGeneration)
    {
        // This separate boundary captures only the window and generation, never RunJobAsync's work delegate.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => _ = ReclaimIdleTransferMemoryAsync(completedGeneration)));
    }

    private async Task ReclaimIdleTransferMemoryAsync(long completedGeneration)
    {
        try { await _idleMemoryCleanup.ReclaimWhenIdleAsync(completedGeneration); }
        catch (Exception ex) { Trace.TraceWarning("Idle managed-memory cleanup failed: {0}", ex.GetType().Name); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _acceptProgress = false;
        _jobCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = T("กำลังยกเลิกอย่างปลอดภัย…");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _sourceUndoTimer?.Stop();
        if (_jobCancellation is null) return;
        e.Cancel = true;
        _closeAfterJob = true;
        Hide();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Close();
    }

    public void ShowSettings()
    {
        _closeAfterJob = false;
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void ShowWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0 || !IsVisible) return;
        MessageBox.Show(this, string.Join(Environment.NewLine, warnings.Take(12).Select(Diagnostic)) + (warnings.Count > 12 ? F("\n… และอีก {0:N0} รายการ", warnings.Count - 12) : ""), T("ข้อสังเกตจากงานล่าสุด"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000_000) return $"{bytes / 1_000_000_000d:0.##} GB";
        if (bytes >= 1_000_000) return $"{bytes / 1_000_000d:0.##} MB";
        if (bytes >= 1_000) return $"{bytes / 1_000d:0.##} KB";
        return $"{bytes:N0} B";
    }

    private sealed record SnapshotRow(SnapshotInfo Snapshot, string Language)
    {
        public string Id => Snapshot.Id;
        public string CreatedDisplay => Snapshot.CreatedUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm", AppLocalization.CultureFor(Language));
        public string DeviceName => Snapshot.DeviceName;
        public string FileCount => Snapshot.FileCount.ToString("N0");
        public string SizeDisplay => FormatBytes(Snapshot.TotalBytes);
    }

    // Throttle before enqueueing and keep only the latest report while the UI is busy.
    private sealed class ThrottledProgress(Action<Action> dispatch, Action<BackupProgress> report) : IProgress<BackupProgress>
    {
        private readonly object _gate = new();
        private long _lastTimestamp;
        private BackupProgress? _latest;
        private bool _pending;

        public void Report(BackupProgress value)
        {
            lock (_gate)
            {
                _latest = value;
                var now = Stopwatch.GetTimestamp();
                if (_pending || now - _lastTimestamp < Stopwatch.Frequency / 8) return;
                _lastTimestamp = now;
                _pending = true;
            }
            dispatch(() =>
            {
                BackupProgress? latest;
                lock (_gate) { latest = _latest; _latest = null; _pending = false; }
                if (latest is not null) report(latest);
            });
        }
    }
}
