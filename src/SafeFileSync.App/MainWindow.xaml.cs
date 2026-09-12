using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
namespace SafeFileSync.App;
public partial class MainWindow : Window
{
    private TransferCoordinator coordinator = new();
    private CancellationTokenSource? cancellation;
    private string? report;
    private DiagnosticCode? supportCode;
    private bool closeWhenStopped;
    private readonly Stopwatch elapsed = new();
    private readonly ObservableCollection<SourceRow> sourceRows = [];
    public MainWindow()
    {
        InitializeComponent(); SourceItems.ItemsSource = sourceRows; AppendSource(new SourceRow());
        StoragePath.Text = RecordStorageLocation.DefaultParent; RefreshHistory(); Closing += OnClosing;
    }
    private void AddSource(object sender, RoutedEventArgs e) { SetPlacement(false); AppendSource(new SourceRow()); }
    private void RemoveSource(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceRow row }) { row.PropertyChanged -= SourceEdited; sourceRows.Remove(row); UpdateSourceRows(); SetPlacement(false); }
    }
    private void AppendSource(SourceRow row) { row.PropertyChanged += SourceEdited; sourceRows.Add(row); UpdateSourceRows(); }
    private void UpdateSourceRows()
    {
        for (int i = 0; i < sourceRows.Count; i++) { sourceRows[i].Number = i + 1; sourceRows[i].Destination = DestinationPath.Text; }
        SourceCount.Text = $"원본 폴더 {sourceRows.Count}개";
    }
    private void SourceEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SourceRow.Path) or nameof(SourceRow.FolderName)) SetPlacement(false);
    }
    private void SetPlacement(bool legacy)
    {
        foreach (var row in sourceRows) row.LegacyPlacement = legacy;
        SourceLayoutNote.Text = legacy ? "이전 단일 원본 작업입니다. 재개하면 목적지 바로 아래에 기존 배치를 유지합니다. 새 비교·복사는 이름별 폴더를 사용합니다." : "각 원본의 내용은 목적지 안의 지정한 폴더에 따로 저장합니다. 같은 폴더 이름은 변경하세요.";
    }
    private void DestinationChanged(object sender, TextChangedEventArgs e)
    {
        foreach (var row in sourceRows) row.Destination = DestinationPath.Text;
    }
    private void BrowseSource(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SourceRow row }) return;
        var dialog = new OpenFolderDialog { Title = "추가할 원본 폴더 선택" };
        if (dialog.ShowDialog(this) == true) row.Path = dialog.FolderName;
    }
    private void BrowseDestination(object sender, RoutedEventArgs e) => Browse(DestinationPath);
    private void BrowseStorage(object sender, RoutedEventArgs e) { Browse(StoragePath); RefreshHistory(); }
    private void ResetStorageLocation(object sender, RoutedEventArgs e) { StoragePath.Text = RecordStorageLocation.DefaultParent; UpdateStoragePreview(); RefreshHistory(); }
    private void UpdateStoragePreview()
    {
        if (string.IsNullOrWhiteSpace(StoragePath.Text)) { StorageActualPath.Text = "실제 기록 폴더: 기준 폴더를 입력하세요."; return; }
        try { StorageActualPath.Text = $"실제 기록 폴더: {RecordStorageLocation.RootFor(StoragePath.Text)} · 겹침 검사 후 생성"; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { StorageActualPath.Text = "실제 기록 폴더: 유효한 기준 폴더를 입력하세요."; }
    }
    private void StorageChanged(object sender, TextChangedEventArgs e)
    {
        if (History is null) return;
        UpdateStoragePreview();
        SetSupportCode(null);
        coordinator = new TransferCoordinator(StoragePath.Text);
        History.ItemsSource = null; report = null; ReportButton.IsEnabled = false;
    }
    private void StorageFocusLost(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => RefreshHistory();
    private void Browse(TextBox box) { var dialog = new OpenFolderDialog { Title = "기존 폴더 선택" }; if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName; }
    private async void Compare(object sender, RoutedEventArgs e) => await Run(false);
    private async void Copy(object sender, RoutedEventArgs e) => await Run(true);
    private void Cancel(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Status.Text = "중지 요청됨 · 진행 중인 시스템 작업이 끝나면 상태를 저장합니다."; }
    private async void Resume(object sender, RoutedEventArgs e)
    {
        SetSupportCode(null);
        if (History.SelectedItem is not HistoryRow selected) { SetSupportCode(DiagnosticCode.InvalidInput); Status.Text = "재개할 작업을 선택하세요."; return; }
        RestoreJob(selected.Info); await Run(true, selected.Info);
    }
    private async void RetryFailed(object sender, RoutedEventArgs e)
    {
        SetSupportCode(null);
        if (History.SelectedItem is not HistoryRow selected) { SetSupportCode(DiagnosticCode.InvalidInput); Status.Text = "재시도할 작업을 선택하세요."; return; }
        RestoreJob(selected.Info); await Run(true, selected.Info, true);
    }
    private void RestoreJob(JobInfo job)
    {
        foreach (var row in sourceRows) row.PropertyChanged -= SourceEdited;
        sourceRows.Clear();
        if (job.Sources is null) AppendSource(new SourceRow(job.Source));
        else foreach (var source in job.Sources) AppendSource(new SourceRow(source.Path, source.FolderName));
        DestinationPath.Text = job.Destination; UpdateSourceRows(); SetPlacement(job.Sources is null);
        Mode.SelectedIndex = (int)job.Mode; ReplaceExisting.IsChecked = job.Conflicts == ConflictPolicy.ReplaceAfterVerification;
    }
    private async Task Run(bool copy, JobInfo? resumedJob = null, bool failedOnly = false)
    {
        SetSupportCode(null);
        bool legacy = resumedJob is { Sources: null };
        SetPlacement(legacy);
        if (sourceRows.Count == 0) { SetSupportCode(DiagnosticCode.InvalidInput); Status.Text = "원본 폴더를 1개 이상 추가하세요."; Summary.Text = "전송·검증이 시작되지 않았습니다."; return; }
        if (sourceRows.Any(row => string.IsNullOrWhiteSpace(row.Path) || (!legacy && string.IsNullOrWhiteSpace(row.FolderName))))
        { SetSupportCode(DiagnosticCode.InvalidInput); Status.Text = "모든 원본 경로와 목적지 안의 폴더 이름을 입력하세요. 빈 행은 제거할 수 있습니다."; Summary.Text = "전송·검증이 시작되지 않았습니다."; return; }
        TransferSource[] sources = sourceRows.Select(row => new TransferSource(row.Path, row.FolderName)).ToArray();
        string destination = DestinationPath.Text;
        var mode = (VerificationMode)Mode.SelectedIndex;
        var conflicts = ReplaceExisting.IsChecked == true ? ConflictPolicy.ReplaceAfterVerification : ConflictPolicy.Preserve;
        cancellation = new(); SetBusy(true); elapsed.Restart(); report = null; ReportButton.IsEnabled = false;
        Status.Text = $"작업 시작 · 원본 {sources.Length}개"; Summary.Text = "검사 중 · 완료 여부 미확정"; Differences.ItemsSource = null; SourceTree.Items.Clear(); DestinationTree.Items.Clear();
        var operation = cancellation; long lastRender = -100;
        var progress = new Progress<TransferProgress>(p => {
            if (!ReferenceEquals(cancellation,operation) || operation.IsCancellationRequested || elapsed.ElapsedMilliseconds-lastRender < 80) return;
            lastRender = elapsed.ElapsedMilliseconds;
            Status.Text = $"{p.Phase} · {p.RelativePath} · {p.Detail}";
            Progress.IsIndeterminate = p.TotalFiles == 0;
            Progress.Value = p.TotalFiles == 0 ? 0 : 100d * p.CompletedFiles / p.TotalFiles;
            double speed = elapsed.Elapsed.TotalSeconds > 0 ? p.CompletedBytes / elapsed.Elapsed.TotalSeconds : 0;
            double remainingSeconds = p.TotalBytes > 0 && speed > 0 ? Math.Max(0,(p.TotalBytes-p.CompletedBytes)/speed) : double.NaN;
            string eta = double.IsNaN(remainingSeconds) ? "계산 중" : remainingSeconds > 86400 ? $"{Math.Ceiling(remainingSeconds/86400):N0}일 이상" : TimeSpan.FromSeconds(remainingSeconds).ToString(@"hh\:mm\:ss");
            Summary.Text = $"처리 {p.CompletedFiles:N0}/{p.TotalFiles:N0} 파일 · {p.CompletedBytes / 1048576d:N1} MiB / {p.TotalBytes / 1048576d:N1} MiB · 전체 경과 기준 {speed / 1048576:N1} MiB/s · {elapsed.Elapsed:hh\\:mm\\:ss} 경과 · 예상 남은 시간 {eta}";
        });
        try {
            var result = await Task.Run(() => legacy
                ? coordinator.RunAsync(resumedJob!.Source, destination, mode, conflicts, copy, progress, operation.Token, resumedJob.Id, failedOnly)
                : coordinator.RunAsync(sources, destination, mode, conflicts, copy, progress, operation.Token, resumedJob?.Id, failedOnly));
            SetSupportCode(result.ErrorCode ?? (result.Info.Status == "NeedsAttention" ? DiagnosticCode.VerificationIncomplete : null));
            var s = result.Summary;
            Status.Text = $"작업: {LocalStatus(result.Info.Status)} · 원본 {sources.Length}개 · 전송 처리: {(result.TransferPercent is null ? "해당 없음" : result.TransferPercent.Value.ToString("F1") + "%")} · 원본 검사: {result.SourceCheck}";
            Progress.Value = result.TransferPercent ?? s.Percent ?? 0;
            Summary.Text = $"검증 일치 {s.Matched:N0} · 누락 {s.Missing:N0} · 불일치 {s.Different:N0} · 목적지 추가 {s.Extra:N0} · 검사 불가 {s.Unverified:N0} · 복제율 {(s.Percent is null ? "빈 폴더 (백분율 해당 없음)" : s.Percent.Value.ToString("F2") + "%")} · SHA-256: {(mode == VerificationMode.Quick ? "전체 검증 미실시" : result.HashPercent is null ? "파일 없음 또는 판단 불가" : result.HashPercent.Value.ToString("F2") + "%")}";
            Differences.ItemsSource = result.Rows.Select(d => new { Path = d.RelativePath, Status = DifferenceStatus(d.Kind), Source = d.Source?.Kind == EntryKind.Directory ? "폴더" : d.Source?.Length.ToString("N0") ?? "없음", Destination = d.Destination?.Kind == EntryKind.Directory ? "폴더" : d.Destination?.Length.ToString("N0") ?? "없음" });
            FillTree(SourceTree, result.Rows.Select(r => r.Source)); FillTree(DestinationTree, result.Rows.Select(r => r.Destination));
            report = result.ReportPath; ReportButton.IsEnabled = report is not null;
        } catch (OperationCanceledException) { SetSupportCode(DiagnosticCode.Cancelled); Status.Text = "중지됨 · 임시 복사본과 작업 기록 보존. 이력에서 재개할 수 있습니다."; Summary.Text = "전송·검증 미완료"; }
        catch (Exception ex) { SetSupportCode(DiagnosticCodes.FromException(ex)); Status.Text = "작업을 완료하지 못했습니다: " + ex.Message; Summary.Text = "전송·검증 미완료 · 원본 삭제/변경 작업은 수행하지 않습니다."; }
        finally {
            cancellation.Dispose(); cancellation = null; Progress.IsIndeterminate = false; elapsed.Stop(); SetBusy(false); RefreshHistory(preserveDiagnostic: true);
            if (closeWhenStopped) Close();
        }
    }
    private void SetBusy(bool busy) { Settings.IsEnabled = CompareButton.IsEnabled = CopyButton.IsEnabled = ResumeButton.IsEnabled = RetryButton.IsEnabled = History.IsEnabled = !busy; CancelButton.IsEnabled = busy; }
    private void RefreshHistory(bool preserveDiagnostic = false)
    {
        try
        {
            History.ItemsSource = coordinator.History().Select(j => new HistoryRow(j, $"{j.StartedUtc} · {LocalStatus(j.Status)} · 원본 {j.Sources?.Count ?? 1}개{(j.Sources is null ? " (이전 배치)" : "")} · {j.Destination}")).ToArray();
            if (History.Items.Count > 0) History.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            if (preserveDiagnostic && supportCode.HasValue) { Status.Text += " · 작업 이력 갱신 실패"; return; }
            SetSupportCode(DiagnosticCodes.FromException(ex)); Status.Text = "작업 이력 읽기 실패: " + ex.Message;
        }
    }
    private void OpenReport(object sender, RoutedEventArgs e)
    {
        if (report is null) return;
        try { Process.Start(new ProcessStartInfo(report) { UseShellExecute = true }); }
        catch (Exception ex) { SetSupportCode(DiagnosticCodes.FromException(ex)); Status.Text = "결과 보고서 열기 실패: " + ex.Message; }
    }
    private void SetSupportCode(DiagnosticCode? code)
    {
        supportCode = code;
        SupportCode.Text = code is { } value ? DiagnosticCodes.Format(value) : "";
        SupportDescription.Text = code is { } described ? DiagnosticCodes.Description(described) : "";
        SupportHint.Text = "코드만 전달하세요. 복사 내용에는 경로·파일명·로그가 포함되지 않습니다.";
        CopySupportCodeButton.IsEnabled = code.HasValue;
        SupportPanel.Visibility = code.HasValue ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CopySupportCode(object sender, RoutedEventArgs e)
    {
        if (supportCode is not { } code) return;
        try
        {
            Clipboard.SetText(DiagnosticCodes.Format(code));
            SupportHint.Text = "코드만 복사했습니다. 경로·파일명·로그는 포함되지 않습니다.";
        }
        catch (Exception)
        {
            SupportHint.Text = "복사하지 못했습니다. 화면에 표시된 오류 코드만 직접 전달하세요.";
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (cancellation is not null) { e.Cancel = true; closeWhenStopped = true; cancellation.Cancel(); Status.Text = "작업을 안전하게 중지한 뒤 종료합니다."; } }
    private static string LocalStatus(string status) => status switch { "Completed" => "완료", "Compared" => "비교 완료", "NeedsAttention" => "확인 필요", "Failed" => "실패", "Cancelled" => "중지됨", _ => "중단되었거나 진행 중 (재검사 필요)" };
    private static string DifferenceStatus(DifferenceKind kind) => kind switch { DifferenceKind.QuickMatch => "크기·시간 일치", DifferenceKind.Verified => "검증 일치", DifferenceKind.Missing => "목적지 누락", DifferenceKind.Extra => "목적지에만 존재", DifferenceKind.Different => "불일치", DifferenceKind.TypeConflict => "파일/폴더 충돌", _ => "검사 불가" };
    private static void FillTree(TreeView tree, IEnumerable<ScanEntry?> entries) {
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => e is not null).Select(e => e!).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)) {
            ItemsControl parent = tree; string key = "";
            foreach (var segment in entry.RelativePath.Split('\\')) {
                key = key.Length == 0 ? segment : key + "\\" + segment;
                if (!nodes.TryGetValue(key, out var node)) { node = new TreeViewItem { Header = segment }; nodes.Add(key,node); parent.Items.Add(node); }
                parent = node;
            }
        }
    }
    private sealed class SourceRow : INotifyPropertyChanged
    {
        private string path = "", folderName = "", destination = "";
        private bool customName, legacyPlacement;
        private int number;
        public SourceRow(string path = "", string? folderName = null)
        {
            Path = path;
            if (folderName is not null) FolderName = folderName;
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        public string Path
        {
            get => path;
            set
            {
                if (path == value) return;
                path = value;
                if (!customName)
                {
                    string trimmed = value.Trim().Replace('/', '\\').TrimEnd('\\');
                    folderName = trimmed[(trimmed.LastIndexOf('\\') + 1)..].TrimEnd(':');
                    Changed(nameof(FolderName)); Changed(nameof(Mapping));
                }
                Changed(nameof(Path));
            }
        }
        public string FolderName { get => folderName; set { customName = true; if (folderName == value) return; folderName = value; Changed(nameof(FolderName)); Changed(nameof(Mapping)); } }
        public string Destination { get => destination; set { destination = value; Changed(nameof(Mapping)); } }
        public bool LegacyPlacement { get => legacyPlacement; set { legacyPlacement = value; Changed(nameof(CanEditFolderName)); Changed(nameof(Mapping)); } }
        public bool CanEditFolderName => !legacyPlacement;
        public string Mapping => legacyPlacement ? $"배치 → {destination} (목적지 바로 아래 · 이전 작업)" : $"배치 → {(string.IsNullOrWhiteSpace(destination) ? "[목적지 폴더]" : destination.TrimEnd('\\'))}\\{(string.IsNullOrWhiteSpace(folderName) ? "[폴더 이름]" : folderName)}";
        public int Number
        {
            get => number;
            set
            {
                number = value;
                foreach (string property in new[] { nameof(PathAutomationId), nameof(FolderAutomationId), nameof(BrowseAutomationId), nameof(RemoveAutomationId), nameof(MappingAutomationId), nameof(PathLabel), nameof(FolderLabel) }) Changed(property);
            }
        }
        public string PathAutomationId => $"SourcePath_{number}";
        public string FolderAutomationId => $"SourceName_{number}";
        public string BrowseAutomationId => $"BrowseSource_{number}";
        public string RemoveAutomationId => $"RemoveSource_{number}";
        public string MappingAutomationId => $"SourceMapping_{number}";
        public string PathLabel => $"원본 폴더 경로 {number}";
        public string FolderLabel => $"목적지 안의 폴더 이름 {number}";
    }
    private sealed record HistoryRow(JobInfo Info, string Display);
}
