using System.Net;
using System.Text;
namespace SafeFileSync.Infrastructure.Windows;
public static class ReportWriter
{
    public static string Write(JobStore db, string source, string destination, string storageRoot)
    {
        string path = Path.Combine(storageRoot, db.Info.Id + "-" + Guid.NewGuid().ToString("N") + ".html");
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
        static string E(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");
        writer.WriteLine("<!doctype html><html lang=\"ko\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>SafeFileSync 결과</title><style>body{font:16px system-ui;margin:40px;line-height:1.6}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:8px;text-align:left;overflow-wrap:anywhere}h1{color:#163f54}</style><h1>SafeFileSync 결과</h1>");
        writer.WriteLine($"<p>작업: {E(db.Info.Id)} / 상태: {E(db.Info.Status)} / 검증: {E(db.Info.Mode)}</p><p>원본: {E(db.Info.Source)}<br>목적지: {E(db.Info.Destination)}<br>원본 검사: {E(db.Get("sourceCheck"))}</p>");
        if (db.Info.Sources is { } sources) {
            writer.WriteLine($"<h2>원본 {sources.Count}개 · 폴더별 배치</h2><table><tr><th>원본 폴더</th><th>목적지 폴더</th></tr>");
            foreach (var item in sources) writer.WriteLine($"<tr><td>{E(item.Path)}</td><td>{E(Path.Combine(db.Info.Destination,item.FolderName))}</td></tr>");
            writer.WriteLine("</table>");
        }
        var summary = db.Summary(source, destination, db.Info.Mode);
        writer.WriteLine($"<p>일치 {summary.Matched} · 누락 {summary.Missing} · 불일치 {summary.Different} · 목적지 추가 {summary.Extra} · 검사 불가 {summary.Unverified}</p><p>빠른 검증은 크기/수정시간 비교입니다. SHA-256은 파일 기본 데이터 스트림만 검증하며 ACL/ADS 완전 복제가 아닙니다. 목적지 추가 파일은 보존합니다.</p><table><tr><th>상대 경로</th><th>검증 상태</th><th>원본 byte</th><th>목적지 byte</th></tr>");
        foreach (var row in db.Compare(source, destination, db.Info.Mode)) writer.WriteLine($"<tr><td>{E(row.RelativePath)}</td><td>{E(row.Kind)}</td><td>{E(row.Source?.Length)}</td><td>{E(row.Destination?.Length)}</td></tr>");
        writer.WriteLine("</table><h2>전송 처리 내역</h2><table><tr><th>경로</th><th>상태</th><th>상세</th></tr>");
        foreach (var row in db.Outcomes()) writer.WriteLine($"<tr><td>{E(row.Path)}</td><td>{E(row.Status)}</td><td>{E(row.Detail)}</td></tr>");
        writer.WriteLine("</table></html>"); return path;
    }
}
