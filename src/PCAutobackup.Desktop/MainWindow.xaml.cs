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
    private CancellationTokenSource? _jobCancellation;
    private bool _loaded;
    private bool _populating;
    private bool _closeAfterCancellation;

    public MainWindow(string? configurationPath = null)
    {
        _service = new ApplicationService(configurationPath);
        InitializeComponent();
        Navigate("Overview");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunJobAsync("กำลังอ่านการตั้งค่า…", async ct =>
        {
            await _service.LoadConfigurationAsync();
            _loaded = true;
            PopulateConfiguration();
            await RefreshRunHistoryAsync();
            StatusText.Text = "พร้อมใช้งาน · ไม่มีงานสำรองเริ่มโดยอัตโนมัติจากหน้าต่างนี้";
        });
    }

    private async Task RefreshRunHistoryAsync()
    {
        var success = await _service.GetLastSuccessAsync();
        LastBackupText.Text = success is null ? "ยังไม่มีข้อมูลการสำรองสำเร็จ" : $"สำรองสำเร็จล่าสุด {LocalDate(success.CompletedUtc ?? success.StartedUtc)} · {success.FilesProcessed:N0} ไฟล์ · {FormatBytes(success.BytesProcessed)}";
        var latest = await _service.GetLastRunAsync();
        if (latest is null)
        {
            LatestRunText.Text = "ยังไม่มีงานสำรองที่บันทึกไว้";
            LatestRunText.Foreground = (Brush)FindResource("Muted");
            return;
        }
        var state = latest.Status switch
        {
            "Succeeded" => "สำเร็จ",
            "Failed" => "ไม่สำเร็จ",
            "Cancelled" => "ยกเลิก",
            "Running" => "เริ่มทำงานแล้ว (สถานะที่บันทึกล่าสุด)",
            _ => latest.Status
        };
        LatestRunText.Text = $"งานล่าสุด · {state} · {LocalDate(latest.CompletedUtc ?? latest.StartedUtc)}" + (latest.Status == "Failed" && !string.IsNullOrWhiteSpace(latest.Message) ? $"\n{latest.Message}" : "");
        LatestRunText.Foreground = latest.Status == "Failed" ? new SolidColorBrush(Color.FromRgb(155, 66, 47)) : (Brush)FindResource("Muted");
    }

    private static string LocalDate(DateTimeOffset value) => value.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.GetCultureInfo("th-TH"));

    private async void ReloadConfiguration_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync("กำลังอ่านการตั้งค่าที่บันทึกไว้…", async ct =>
        {
            await _service.LoadConfigurationAsync();
            _loaded = true;
            PopulateConfiguration();
            ClearSnapshotList();
            await RefreshRunHistoryAsync();
            StatusText.Text = "อ่านการตั้งค่าและสถานะที่บันทึกไว้แล้ว";
        });
    }

    private void PopulateConfiguration()
    {
        _populating = true;
        var config = _service.Configuration;
        DeviceText.Text = config.DeviceName;
        GoogleRadio.IsChecked = config.Destination == DestinationKind.GoogleDrive;
        ExternalRadio.IsChecked = config.Destination == DestinationKind.ExternalDrive;
        LocalRadio.IsChecked = config.Destination == DestinationKind.LocalFolder;
        LocalDestinationText.Text = string.IsNullOrWhiteSpace(config.LocalDestinationPath) ? "ยังไม่ได้เลือกโฟลเดอร์" : config.LocalDestinationPath;
        GoogleAccountText.Text = string.IsNullOrWhiteSpace(config.GoogleEmail) ? "ยังไม่ได้เชื่อมต่อบัญชี" : $"เชื่อมต่อแล้ว · {config.GoogleEmail}";
        DisconnectButton.IsEnabled = !string.IsNullOrWhiteSpace(config.ProtectedGoogleTokens);
        ClientIdBox.Text = config.GoogleClientId;
        ClientSecretBox.Password = config.GoogleClientSecret;
        ScheduleEnabledBox.IsChecked = config.ScheduleEnabled;
        DailyTimeBox.Text = config.DailyTime;
        RetentionBox.Text = config.RetentionDays.ToString(CultureInfo.InvariantCulture);
        QuotaBox.Text = config.QuotaBytes is { } quota ? (quota / 1_000_000_000m).ToString("0.#########", CultureInfo.InvariantCulture) : "";
        InventoryBox.IsChecked = config.IncludeApplicationInventory;
        SourceCountText.Text = config.Sources.Count.ToString("N0");
        SourcesList.ItemsSource = null;
        SourcesList.ItemsSource = config.Sources;
        EmptySources.Visibility = config.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleText.Text = config.ScheduleEnabled ? config.DailyTime : "ยังไม่เปิด";
        ScheduleCaption.Text = config.ScheduleEnabled ? "ทุกวัน ตามเวลาของ Windows" : "ตั้งเวลาได้ในการตั้งค่า";
        RetentionText.Text = config.RetentionDays == 0 ? "ชุดล่าสุด" : $"{config.RetentionDays:N0} วัน";
        DestinationSummary.Text = config.Destination == DestinationKind.GoogleDrive
            ? "Google Drive · " + (config.GoogleEmail ?? "ยังไม่ได้เชื่อมต่อ")
            : (config.Destination == DestinationKind.ExternalDrive ? "HDD / SSD ภายนอก" : "โฟลเดอร์ในเครื่อง") + " · " + (string.IsNullOrWhiteSpace(config.LocalDestinationPath) ? "ยังไม่ได้เลือก" : config.LocalDestinationPath);
        var hasKey = !string.IsNullOrWhiteSpace(config.ProtectedRecoveryKey);
        KeyStateText.Text = config.RecoveryKeyAcknowledged && hasKey ? "มีกุญแจในเครื่อง และเคยส่งออกหรือนำเข้าแล้ว" : "ยังต้องส่งออกกุญแจกู้คืน ก่อนเริ่มสำรอง";
        KeySummary.Text = config.RecoveryKeyAcknowledged && hasKey ? "เก็บสำเนากุญแจไว้นอกเครื่องนี้เสมอ เพื่อใช้เมื่อเครื่องเดิมเข้าไม่ได้" : "เก็บไฟล์กุญแจไว้นอกเครื่องนี้ เพื่อกู้คืนได้เมื่อเปลี่ยน PC หรือ Windows เสีย";
        var destinationReady = config.Destination == DestinationKind.GoogleDrive
            ? !string.IsNullOrWhiteSpace(config.ProtectedGoogleTokens)
            : !string.IsNullOrWhiteSpace(config.LocalDestinationPath);
        var ready = config.Sources.Count > 0 && destinationReady && hasKey && config.RecoveryKeyAcknowledged;
        ReadinessTitle.Text = ready ? "พร้อมสำรองเมื่อคุณต้องการ" : "เริ่มต้นปกป้องไฟล์ของคุณ";
        ReadinessText.Text = ready ? "สำรองไฟล์ที่เปลี่ยนแปลง และเก็บชุดที่กู้คืนได้ตามระยะเวลาที่ตั้งไว้" : "เลือกโฟลเดอร์ พื้นที่จัดเก็บ และเก็บกุญแจกู้คืนก่อนสำรองครั้งแรก";
        UpdateDestinationPanels();
        _populating = false;
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate((string)((Button)sender).Tag);

    private void Navigate(string page)
    {
        var pages = new Dictionary<string, (StackPanel Panel, Button Nav, string Title, string Subtitle)>
        {
            ["Overview"] = (OverviewPage, OverviewNav, "ภาพรวม", "ดูแลไฟล์สำคัญของคุณ ในจังหวะที่ตั้งไว้"),
            ["Sources"] = (SourcesPage, SourcesNav, "โฟลเดอร์ต้นทาง", "เลือกไฟล์สำคัญ โดยไม่ต้องสำรองทั้งเครื่อง"),
            ["Destination"] = (DestinationPage, DestinationNav, "พื้นที่จัดเก็บ", "ให้ไฟล์มีบ้านอีกแห่งที่คุณเข้าถึงได้"),
            ["History"] = (HistoryPage, HistoryNav, "ประวัติและกู้คืน", "ย้อนกลับไปยังชุดสำรองที่เสร็จสมบูรณ์"),
            ["Settings"] = (SettingsPage, SettingsNav, "การตั้งค่า", "กำหนดเวลา ระยะเวลาเก็บ และดูแลกุญแจกู้คืน")
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
        var dialog = new OpenFolderDialog { Title = "เลือกโฟลเดอร์ต้นทาง", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync("กำลังบันทึกโฟลเดอร์…", async ct =>
        {
            foreach (var path in dialog.FolderNames)
            {
                if (_service.Configuration.Sources.Any(s => string.Equals(Path.TrimEndingDirectorySeparator(s.Path), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))) continue;
                _service.Configuration.Sources.Add(new BackupSource { Path = path, Label = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path });
            }
            await _service.SaveConfigurationAsync();
            PopulateConfiguration();
            StatusText.Text = "บันทึกโฟลเดอร์ต้นทางแล้ว";
        });
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        await RunJobAsync("กำลังบันทึกโฟลเดอร์…", async ct =>
        {
            _service.Configuration.Sources.RemoveAll(source => source.Id == id);
            await _service.SaveConfigurationAsync();
            PopulateConfiguration();
            StatusText.Text = "นำโฟลเดอร์ออกจากการสำรองแล้ว · ไฟล์ต้นฉบับยังอยู่ตามเดิม";
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
        await RunJobAsync("กำลังเปลี่ยนพื้นที่จัดเก็บ…", async ct =>
        {
            _service.Configuration.Destination = GoogleRadio.IsChecked == true ? DestinationKind.GoogleDrive : ExternalRadio.IsChecked == true ? DestinationKind.ExternalDrive : DestinationKind.LocalFolder;
            await _service.SaveConfigurationAsync();
            PopulateConfiguration();
            ClearSnapshotList();
            StatusText.Text = "บันทึกประเภทพื้นที่จัดเก็บแล้ว";
        });
    }

    private async void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "เลือกโฟลเดอร์เฉพาะสำหรับคลังสำรอง" };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync("กำลังบันทึกพื้นที่จัดเก็บ…", async ct =>
        {
            _service.Configuration.LocalDestinationPath = dialog.FolderName;
            _service.Configuration.ExternalVolumeId = null;
            await _service.SaveConfigurationAsync();
            PopulateConfiguration();
            ClearSnapshotList();
            StatusText.Text = "บันทึกพื้นที่จัดเก็บแล้ว · ยังไม่ได้เริ่มสำรอง";
        });
    }

    private async void ImportClient_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "นำเข้า Google Desktop OAuth client", Filter = "OAuth client JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync("กำลังนำเข้าการตั้งค่า OAuth…", async ct =>
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 1024 * 1024) throw new InvalidOperationException("ไฟล์ OAuth client มีขนาดใหญ่เกินไป");
            await _service.ImportGoogleClientAsync(await File.ReadAllTextAsync(dialog.FileName, ct));
            PopulateConfiguration();
            StatusText.Text = "นำเข้า OAuth client แล้ว · กดเชื่อมต่อ Google เพื่ออนุญาตบัญชี";
        });
    }

    private async void SaveClient_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync("กำลังบันทึก OAuth client…", async ct =>
        {
            if (string.IsNullOrWhiteSpace(ClientIdBox.Text)) throw new InvalidOperationException("กรุณาระบุ Client ID ของ Google Desktop app");
            var json = System.Text.Json.JsonSerializer.Serialize(new { installed = new { client_id = ClientIdBox.Text.Trim(), client_secret = ClientSecretBox.Password } });
            await _service.ImportGoogleClientAsync(json);
            PopulateConfiguration();
            StatusText.Text = "บันทึก OAuth client แล้ว";
        });
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync("กำลังรอการอนุญาตจาก Google ในเบราว์เซอร์…", async ct =>
        {
            await _service.SignInGoogleAsync(string.IsNullOrWhiteSpace(EmailHintBox.Text) ? null : EmailHintBox.Text.Trim(), ct);
            PopulateConfiguration();
            StatusText.Text = $"Google ยืนยันบัญชี {_service.Configuration.GoogleEmail} แล้ว";
        });
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync("กำลังยกเลิกการเชื่อมต่อ…", async ct =>
        {
            await _service.DisconnectGoogleAsync();
            PopulateConfiguration();
            ClearSnapshotList();
            StatusText.Text = "ยกเลิกการเชื่อมต่อ Google ในเครื่องนี้แล้ว";
        });
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync("กำลังบันทึกการตั้งค่า…", async ct =>
        {
            if (!TimeOnly.TryParseExact(DailyTimeBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new InvalidOperationException("ระบุเวลาแบบ 24 ชั่วโมง เช่น 21:00");
            if (!int.TryParse(RetentionBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 0) throw new InvalidOperationException("จำนวนวันต้องเป็นจำนวนเต็มตั้งแต่ 0 ขึ้นไป");
            long? quota = null;
            if (!string.IsNullOrWhiteSpace(QuotaBox.Text))
            {
                if (!decimal.TryParse(QuotaBox.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var gb) || gb <= 0 || gb > long.MaxValue / 1_000_000_000m) throw new InvalidOperationException("โควตาต้องมากกว่า 0 GB หรือเว้นว่างเพื่อไม่จำกัด");
                quota = checked((long)(gb * 1_000_000_000m));
                if (quota == 0) throw new InvalidOperationException("โควตามีขนาดเล็กเกินไป");
            }
            _service.Configuration.DailyTime = DailyTimeBox.Text.Trim();
            _service.Configuration.RetentionDays = days;
            _service.Configuration.QuotaBytes = quota;
            await _service.SetScheduleAsync(ScheduleEnabledBox.IsChecked == true);
            PopulateConfiguration();
            StatusText.Text = "บันทึกการตั้งค่าและตารางเวลาของ Windows แล้ว";
        });
    }

    private async void Inventory_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _populating) return;
        await RunJobAsync("กำลังบันทึกการตั้งค่า…", async ct =>
        {
            _service.Configuration.IncludeApplicationInventory = InventoryBox.IsChecked == true;
            await _service.SaveConfigurationAsync();
            StatusText.Text = "บันทึกการแนบรายชื่อแอปแล้ว";
        });
    }

    private async void ExportKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "ส่งออกกุญแจ — เก็บสำเนาไว้นอกเครื่องนี้", Filter = "Recovery key (*.pcakey)|*.pcakey", FileName = "PCAutobackup-recovery.pcakey", AddExtension = true, DefaultExt = ".pcakey" };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync("กำลังส่งออกกุญแจกู้คืน…", async ct =>
        {
            await _service.ExportRecoveryKeyAsync(dialog.FileName);
            PopulateConfiguration();
            StatusText.Text = "ส่งออกกุญแจแล้ว · เก็บสำเนาไว้นอกเครื่องนี้ในที่ปลอดภัย";
            MessageBox.Show(this, "ส่งออกกุญแจกู้คืนแล้ว\n\nเก็บสำเนาไว้นอก PC นี้ เช่น USB ที่ปลอดภัย และอย่าส่งให้ผู้อื่น หากกุญแจหาย จะไม่สามารถถอดรหัสคลังสำรองได้", "เก็บกุญแจกู้คืนให้ปลอดภัย", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async void ImportKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "เลือกกุญแจกู้คืนจากเครื่องเดิม", Filter = "Recovery key (*.pcakey;*.txt)|*.pcakey;*.txt|ทุกไฟล์ (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var hasBackupKey = !string.IsNullOrWhiteSpace(_service.Configuration.ProtectedRecoveryKey);
        await RunJobAsync("กำลังนำเข้ากุญแจกู้คืน…", async ct =>
        {
            await _service.ImportRecoveryKeyAsync(dialog.FileName);
            PopulateConfiguration();
            ClearSnapshotList();
            StatusText.Text = hasBackupKey
                ? "นำเข้ากุญแจสำหรับกู้คืนในครั้งนี้แล้ว · กุญแจสำรองอัตโนมัติเดิมยังคงอยู่"
                : "นำเข้ากุญแจแล้ว · เปิดคลังสำรองเดิมเพื่ออ่านประวัติ";
        });
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync("กำลังเตรียมสำรองข้อมูล…", async ct =>
        {
            var result = await _service.BackupAsync(CreateProgress(), ct);
            LastBackupText.Text = $"สำรองสำเร็จล่าสุด {DateTime.Now:dd/MM/yyyy HH:mm} · {result.FileCount:N0} ไฟล์ · {FormatBytes(result.TotalBytes)}";
            StatusText.Text = $"สำรองสำเร็จ · {result.FileCount:N0} ไฟล์ · ส่งข้อมูล {FormatBytes(result.UploadedBytes)}";
            ShowWarnings(result.Warnings);
        });
    }

    private async void RefreshSnapshots_Click(object sender, RoutedEventArgs e) => await RunJobAsync("กำลังอ่านคลังสำรอง…", ct => LoadSnapshotsAsync(ct));

    private async Task LoadSnapshotsAsync(CancellationToken ct)
    {
        var snapshots = await _service.ListSnapshotsAsync(ct);
        SnapshotsList.ItemsSource = snapshots.OrderByDescending(s => s.CreatedUtc).Select(s => new SnapshotRow(s)).ToList();
        EmptyHistory.Text = snapshots.Count == 0 ? "ยังไม่พบชุดสำรองที่เสร็จสมบูรณ์ในคลังนี้" : $"พบ {snapshots.Count:N0} ชุดสำรอง";
        StatusText.Text = $"อ่านประวัติสำรองแล้ว · {snapshots.Count:N0} ชุด";
    }

    private void ClearSnapshotList()
    {
        SnapshotsList.ItemsSource = null;
        EmptyHistory.Text = "กดรีเฟรชเพื่ออ่านชุดสำรองจากคลังที่เลือก";
    }

    private async void OpenLocalRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "เลือกโฟลเดอร์คลังสำรองเดิม (ไม่ใช่โฟลเดอร์ที่จะรับไฟล์)" };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync("กำลังเปิดคลังสำรองเดิม…", async ct =>
        {
            ClearSnapshotList();
            await _service.SelectLocalRepositoryAsync(dialog.FolderName, ct);
            RepositoryText.Text = "คลังกู้คืน · " + dialog.FolderName;
            await LoadSnapshotsAsync(ct);
        });
    }

    private async void FindGoogleRepositories_Click(object sender, RoutedEventArgs e)
    {
        await RunJobAsync("กำลังค้นหาคลังสำรองใน Google Drive…", async ct =>
        {
            var repositories = await _service.ListGoogleRepositoriesAsync(ct);
            GoogleRepositoriesBox.DisplayMemberPath = nameof(GoogleRepository.DeviceName);
            GoogleRepositoriesBox.ItemsSource = repositories;
            GoogleRepositoriesBox.Visibility = Visibility.Visible;
            StatusText.Text = repositories.Count == 0 ? "ไม่พบคลังที่แอปมีสิทธิ์เข้าถึงในบัญชีนี้" : $"พบ {repositories.Count:N0} คลัง · เลือกเครื่องจากรายการ";
        });
    }

    private async void GoogleRepository_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GoogleRepositoriesBox.SelectedItem is not GoogleRepository repository || _jobCancellation is not null) return;
        await RunJobAsync("กำลังเปิดคลังสำรอง Google Drive…", async ct =>
        {
            ClearSnapshotList();
            await _service.SelectGoogleRepositoryAsync(repository.Id, ct);
            RepositoryText.Text = "คลังกู้คืน Google Drive · " + repository.DeviceName;
            await LoadSnapshotsAsync(ct);
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotRow row)
        {
            MessageBox.Show(this, "เลือกชุดสำรองจากรายการก่อนกู้คืน", "เลือกชุดสำรอง", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFolderDialog { Title = "เลือกโฟลเดอร์ว่างสำหรับรับไฟล์ที่กู้คืน" };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync("กำลังตรวจสอบและกู้คืนไฟล์…", async ct =>
        {
            if (Directory.EnumerateFileSystemEntries(dialog.FolderName).Any()) throw new InvalidOperationException("กรุณาเลือกโฟลเดอร์ว่าง เพื่อแยกไฟล์ที่กู้คืนจากไฟล์เดิมอย่างชัดเจน");
            var result = await _service.RestoreAsync(row.Id, dialog.FolderName, CreateProgress(), ct);
            StatusText.Text = $"กู้คืนสำเร็จ · {result.FilesRestored:N0} ไฟล์ · {FormatBytes(result.BytesRestored)}";
            ShowWarnings(result.Warnings);
        });
    }

    private IProgress<BackupProgress> CreateProgress() => new ThrottledProgress(progress => Dispatcher.BeginInvoke(() =>
    {
        if (_jobCancellation is null) return;
        StatusText.Text = TranslateStage(progress.Stage);
        ProgressDetailText.Text = $"{progress.FilesProcessed:N0} ไฟล์ · {FormatBytes(progress.BytesProcessed)}" + (string.IsNullOrEmpty(progress.CurrentFile) ? "" : " · " + progress.CurrentFile);
        ProgressDetailText.ToolTip = ProgressDetailText.Text;
    }));

    private static string TranslateStage(string stage) => stage.ToLowerInvariant() switch
    {
        "scanning" or "scan" => "กำลังอ่านรายการไฟล์…",
        "backingup" or "backup" or "uploading" => "กำลังเข้ารหัสและสำรองไฟล์…",
        "committing" or "commit" => "กำลังยืนยันชุดสำรอง…",
        "pruning" or "retention" => "กำลังจัดเก็บประวัติตามการตั้งค่า…",
        "restoring" or "restore" => "กำลังตรวจสอบและกู้คืนไฟล์…",
        _ => "กำลังทำงาน · " + stage
    };

    private async Task RunJobAsync(string initialStatus, Func<CancellationToken, Task> work)
    {
        if (_jobCancellation is not null) return;
        _jobCancellation = new CancellationTokenSource();
        ReloadConfigurationButton.IsEnabled = false;
        MainContent.IsEnabled = false;
        JobProgress.Visibility = Visibility.Visible;
        ProgressDetailText.Visibility = Visibility.Visible;
        ProgressDetailText.Text = "";
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        StatusText.Text = initialStatus;
        try
        {
            await work(_jobCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "ยกเลิกงานแล้ว · ชุดสำรองที่สำเร็จก่อนหน้ายังคงอยู่";
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            try
            {
                await _service.LoadConfigurationAsync();
                _loaded = true;
                PopulateConfiguration();
            }
            catch
            {
                _loaded = false;
                ReadinessTitle.Text = "ยังอ่านการตั้งค่าที่บันทึกไว้ไม่ได้";
                ReadinessText.Text = "ตรวจสอบไฟล์การตั้งค่า แล้วกดรีเฟรชการตั้งค่าอีกครั้ง";
                message += "\n\nอ่านการตั้งค่าที่บันทึกไว้ไม่ได้ กรุณาตรวจสอบแล้วกดรีเฟรชการตั้งค่า";
            }
            StatusText.Text = "ทำงานไม่สำเร็จ · " + ex.Message;
            MessageBox.Show(this, message, "ทำงานไม่สำเร็จ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            try { if (_loaded) await RefreshRunHistoryAsync(); }
            catch { LatestRunText.Text = "อ่านสถานะงานล่าสุดไม่ได้ · กดรีเฟรชการตั้งค่าเพื่อลองอีกครั้ง"; }
            _jobCancellation.Dispose();
            _jobCancellation = null;
            ReloadConfigurationButton.IsEnabled = true;
            MainContent.IsEnabled = _loaded;
            JobProgress.Visibility = Visibility.Collapsed;
            ProgressDetailText.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            if (_closeAfterCancellation) Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _jobCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "กำลังยกเลิกอย่างปลอดภัย…";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_jobCancellation is null) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "มีงานกำลังทำอยู่ ต้องการยกเลิกงานและปิดแอปหรือไม่?", "ปิดแอป", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _closeAfterCancellation = true;
        Cancel_Click(this, new RoutedEventArgs());
    }

    private void ShowWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;
        MessageBox.Show(this, string.Join(Environment.NewLine, warnings.Take(12)) + (warnings.Count > 12 ? $"\n… และอีก {warnings.Count - 12:N0} รายการ" : ""), "ข้อสังเกตจากงานล่าสุด", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000_000) return $"{bytes / 1_000_000_000d:0.##} GB";
        if (bytes >= 1_000_000) return $"{bytes / 1_000_000d:0.##} MB";
        if (bytes >= 1_000) return $"{bytes / 1_000d:0.##} KB";
        return $"{bytes:N0} B";
    }

    private sealed record SnapshotRow(SnapshotInfo Snapshot)
    {
        public string Id => Snapshot.Id;
        public string CreatedDisplay => Snapshot.CreatedUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.GetCultureInfo("th-TH"));
        public string DeviceName => Snapshot.DeviceName;
        public string FileCount => Snapshot.FileCount.ToString("N0");
        public string SizeDisplay => FormatBytes(Snapshot.TotalBytes);
    }

    // Throttle before enqueueing on the UI thread so fast streaming workers cannot grow the dispatcher queue.
    private sealed class ThrottledProgress(Action<BackupProgress> report) : IProgress<BackupProgress>
    {
        private long _lastTimestamp;

        public void Report(BackupProgress value)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastTimestamp);
            if (now - previous < Stopwatch.Frequency / 8) return;
            if (Interlocked.CompareExchange(ref _lastTimestamp, now, previous) != previous) return;
            report(value);
        }
    }
}
