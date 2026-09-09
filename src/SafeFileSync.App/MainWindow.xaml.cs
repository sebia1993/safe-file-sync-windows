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
    private readonly TransferCoordinator coordinator = new();
    private CancellationTokenSource? cancellation;
    private string? report;
    private bool closeWhenStopped;
    private readonly Stopwatch elapsed = new();
    public MainWindow() { InitializeComponent(); RefreshHistory(); Closing += OnClosing; }
    private void BrowseSource(object sender, RoutedEventArgs e) => Browse(SourcePath);
    private void BrowseDestination(object sender, RoutedEventArgs e) => Browse(DestinationPath);
    private void Browse(TextBox box) { var dialog = new OpenFolderDialog { Title = "기존 폴더 선택" }; if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName; }
    private async void Compare(object sender, RoutedEventArgs e) => await Run(false);
    private async void Copy(object sender, RoutedEventArgs e) => await Run(true);
    private void Cancel(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Status.Text = "중지 요청됨 · 진행 중인 시스템 작업이 끝나면 상태를 저장합니다."; }
    private async void Resume(object sender, RoutedEventArgs e)
    {
        if (History.SelectedItem is not HistoryRow selected) { Status.Text = "재개할 작업을 선택하세요."; return; }
        SourcePath.Text = selected.Info.Source; DestinationPath.Text = selected.Info.Destination;
        Mode.SelectedIndex = (int)selected.Info.Mode; ReplaceExisting.IsChecked = selected.Info.Conflicts == ConflictPolicy.ReplaceAfterVerification;
        await Run(true, selected.Info.Id);
    }
    private async void RetryFailed(object sender, RoutedEventArgs e)
    {
        if (History.SelectedItem is not HistoryRow selected) { Status.Text = "재시도할 작업을 선택하세요."; return; }
        SourcePath.Text = selected.Info.Source; DestinationPath.Text = selected.Info.Destination;
        Mode.SelectedIndex = (int)selected.Info.Mode; ReplaceExisting.IsChecked = selected.Info.Conflicts == ConflictPolicy.ReplaceAfterVerification;
        await Run(true,selected.Info.Id,true);
    }
    private async Task Run(bool copy, string? id = null, bool failedOnly = false)
    {
        string source = SourcePath.Text, destination = DestinationPath.Text;
        var mode = (VerificationMode)Mode.SelectedIndex;
        var conflicts = ReplaceExisting.IsChecked == true ? ConflictPolicy.ReplaceAfterVerification : ConflictPolicy.Preserve;
        cancellation = new(); SetBusy(true); elapsed.Restart(); report = null; ReportButton.IsEnabled = false;
        Summary.Text = "검사 중 · 완료 여부 미확정"; Differences.ItemsSource = null; SourceTree.Items.Clear(); DestinationTree.Items.Clear();
        var progress = new Progress<TransferProgress>(p => {
            Status.Text = $"{p.Phase} · {p.RelativePath} · {p.Detail}";
            Progress.IsIndeterminate = p.TotalFiles == 0;
            Progress.Value = p.TotalFiles == 0 ? 0 : 100d * p.CompletedFiles / p.TotalFiles;
            double speed = elapsed.Elapsed.TotalSeconds > 0 ? p.CompletedBytes / elapsed.Elapsed.TotalSeconds : 0;
            Summary.Text = $"처리 {p.CompletedFiles:N0}/{p.TotalFiles:N0} 파일 · {p.CompletedBytes / 1048576d:N1} MiB / {p.TotalBytes / 1048576d:N1} MiB · 전체 경과 기준 {speed / 1048576:N1} MiB/s · {elapsed.Elapsed:hh\\:mm\\:ss} 경과";
        });
        try {
            var result = await Task.Run(() => coordinator.RunAsync(source, destination, mode, conflicts, copy, progress, cancellation.Token, id, failedOnly));
            var s = result.Summary;
            Status.Text = $"작업: {LocalStatus(result.Info.Status)} · 전송 처리: {(result.TransferPercent is null ? "해당 없음" : result.TransferPercent.Value.ToString("F1") + "%")} · 원본 검사: {result.SourceCheck}";
            Progress.Value = result.TransferPercent ?? s.Percent ?? 0;
            Summary.Text = $"검증 일치 {s.Matched:N0} · 누락 {s.Missing:N0} · 불일치 {s.Different:N0} · 목적지 추가 {s.Extra:N0} · 검사 불가 {s.Unverified:N0} · 복제율 {(s.Percent is null ? "빈 폴더 (백분율 해당 없음)" : s.Percent.Value.ToString("F2") + "%")} · SHA-256: {(mode == VerificationMode.Quick ? "전체 검증 미실시" : result.HashPercent is null ? "파일 없음 또는 판단 불가" : result.HashPercent.Value.ToString("F2") + "%")}";
            Differences.ItemsSource = result.Rows.Select(d => new { Path = d.RelativePath, Status = DifferenceStatus(d.Kind), Source = d.Source?.Kind == EntryKind.Directory ? "폴더" : d.Source?.Length.ToString("N0") ?? "없음", Destination = d.Destination?.Kind == EntryKind.Directory ? "폴더" : d.Destination?.Length.ToString("N0") ?? "없음" });
            FillTree(SourceTree, result.Rows.Select(r => r.Source)); FillTree(DestinationTree, result.Rows.Select(r => r.Destination));
            report = result.ReportPath; ReportButton.IsEnabled = report is not null;
        } catch (OperationCanceledException) { Status.Text = "중지됨 · 임시 복사본과 작업 기록 보존. 이력에서 재개할 수 있습니다."; Summary.Text = "전송·검증 미완료"; }
        catch (Exception ex) { Status.Text = "작업을 완료하지 못했습니다: " + ex.Message; Summary.Text = "전송·검증 미완료 · 원본 삭제/변경 작업은 수행하지 않습니다."; }
        finally {
            cancellation.Dispose(); cancellation = null; Progress.IsIndeterminate = false; elapsed.Stop(); SetBusy(false); RefreshHistory();
            if (closeWhenStopped) Close();
        }
    }
    private void SetBusy(bool busy) { Settings.IsEnabled = CompareButton.IsEnabled = CopyButton.IsEnabled = ResumeButton.IsEnabled = RetryButton.IsEnabled = History.IsEnabled = !busy; CancelButton.IsEnabled = busy; }
    private void RefreshHistory() { try { History.ItemsSource = coordinator.History().Select(j => new HistoryRow(j, $"{j.StartedUtc} · {LocalStatus(j.Status)} · {j.Destination}")).ToArray(); } catch (Exception ex) { Status.Text = "작업 이력 읽기 실패: " + ex.Message; } }
    private void OpenReport(object sender, RoutedEventArgs e) { if (report is not null) Process.Start(new ProcessStartInfo(report) { UseShellExecute = true }); }
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
    private sealed record HistoryRow(JobInfo Info, string Display);
}
