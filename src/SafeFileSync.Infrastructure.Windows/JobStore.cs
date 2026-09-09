using System.Text.Json;
using Microsoft.Data.Sqlite;
using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;

public sealed class JobStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly Dictionary<string,(string Status,string Detail)> pendingOutcomes = new(StringComparer.OrdinalIgnoreCase);
    public string DatabasePath { get; }
    public JobInfo Info => JsonSerializer.Deserialize<JobInfo>(Get("job")!)!;
    public JobStore(string path, bool readOnly = false)
    {
        DatabasePath = path;
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        if (!readOnly) Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA temp_store=MEMORY; CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY,v TEXT NOT NULL); CREATE TABLE IF NOT EXISTS entries(snapshot TEXT NOT NULL,k TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(snapshot,k)); CREATE TABLE IF NOT EXISTS outcomes(k TEXT PRIMARY KEY,status TEXT NOT NULL,detail TEXT NOT NULL);");
    }
    public void Initialize(JobInfo info) => Set("job", JsonSerializer.Serialize(info));
    public void SetStatus(string status) { FlushOutcomes(); Initialize(Info with { Status = status }); }
    public string? Get(string key)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT v FROM meta WHERE k=$k";
        command.Parameters.AddWithValue("$k", key); return command.ExecuteScalar() as string;
    }
    public void Set(string key, string value)
    {
        using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO meta VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=excluded.v";
        command.Parameters.AddWithValue("$k", key); command.Parameters.AddWithValue("$v", value); command.ExecuteNonQuery();
    }
    public void SaveSnapshot(string name, IEnumerable<ScanEntry> entries, CancellationToken token, Action<long,long>? progress = null)
    {
        Set("complete:" + name, "0");
        using var tx = connection.BeginTransaction();
        using var clear = connection.CreateCommand(); clear.Transaction = tx;
        clear.CommandText = "DELETE FROM entries WHERE snapshot=$s"; clear.Parameters.AddWithValue("$s", name); clear.ExecuteNonQuery();
        using var insert = connection.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO entries VALUES($s,$k,$j) ON CONFLICT(snapshot,k) DO UPDATE SET json=$j";
        insert.Parameters.AddWithValue("$s", name); var key = insert.Parameters.Add("$k", SqliteType.Text); var json = insert.Parameters.Add("$j", SqliteType.Text);
        long count = 0, bytes = 0;
        foreach (var entry in entries) {
            token.ThrowIfCancellationRequested(); key.Value = entry.RelativePath.ToUpperInvariant(); json.Value = JsonSerializer.Serialize(entry);
            using (var check = connection.CreateCommand()) {
                check.Transaction = tx; check.CommandText = "SELECT json FROM entries WHERE snapshot=$s AND k=$k";
                check.Parameters.AddWithValue("$s", name); check.Parameters.AddWithValue("$k", key.Value);
                if (check.ExecuteScalar() is string old && JsonSerializer.Deserialize<ScanEntry>(old)!.RelativePath != entry.RelativePath)
                    throw new IOException("대소문자로만 구분되는 항목은 지원하지 않습니다: " + entry.RelativePath);
            }
            insert.ExecuteNonQuery(); count++; bytes += entry.Length;
            if (count % 128 == 0) progress?.Invoke(count, bytes);
        }
        token.ThrowIfCancellationRequested(); tx.Commit(); Set("complete:" + name, "1"); progress?.Invoke(count, bytes);
    }
    public IEnumerable<ScanEntry> Entries(string snapshot)
    {
        RequireComplete(snapshot);
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT json FROM entries WHERE snapshot=$s ORDER BY k"; cmd.Parameters.AddWithValue("$s", snapshot);
        using var reader = cmd.ExecuteReader(); while (reader.Read()) yield return JsonSerializer.Deserialize<ScanEntry>(reader.GetString(0))!;
    }
    public IEnumerable<Difference> Compare(string source, string destination, VerificationMode mode)
    {
        RequireComplete(source); RequireComplete(destination);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT a.json,b.json FROM entries a LEFT JOIN entries b ON b.snapshot=$d AND a.k=b.k WHERE a.snapshot=$s UNION ALL SELECT NULL,b.json FROM entries b LEFT JOIN entries a ON a.snapshot=$s AND a.k=b.k WHERE b.snapshot=$d AND a.k IS NULL";
        cmd.Parameters.AddWithValue("$s", source); cmd.Parameters.AddWithValue("$d", destination);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return EntryComparer.Compare(reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<ScanEntry>(reader.GetString(0)), reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<ScanEntry>(reader.GetString(1)), mode);
    }
    public ComparisonSummary Summary(string source, string destination, VerificationMode mode)
    {
        long match = 0, missing = 0, different = 0, extra = 0, unverified = 0;
        foreach (var row in Compare(source, destination, mode))
            switch (row.Kind) {
                case DifferenceKind.QuickMatch: case DifferenceKind.Verified: match++; break;
                case DifferenceKind.Missing: missing++; break;
                case DifferenceKind.Extra: extra++; break;
                case DifferenceKind.Unverified: unverified++; break;
                default: different++; break;
            }
        return new(match, missing, different, extra, unverified);
    }
    public void Outcome(string relative, string status, string detail = "")
    {
        pendingOutcomes[relative] = (status,detail);
        if (pendingOutcomes.Count >= 128) FlushOutcomes();
    }
    public void FlushOutcomes()
    {
        if (pendingOutcomes.Count == 0) return;
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO outcomes VALUES($k,$s,$d) ON CONFLICT(k) DO UPDATE SET status=$s,detail=$d";
        var key = cmd.Parameters.Add("$k",SqliteType.Text); var status = cmd.Parameters.Add("$s",SqliteType.Text); var detail = cmd.Parameters.Add("$d",SqliteType.Text);
        foreach (var item in pendingOutcomes) { key.Value=item.Key; status.Value=item.Value.Status; detail.Value=item.Value.Detail; cmd.ExecuteNonQuery(); }
        tx.Commit(); pendingOutcomes.Clear();
    }
    public IEnumerable<(string Path,string Status,string Detail)> Outcomes()
    {
        FlushOutcomes();
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT k,status,detail FROM outcomes ORDER BY k";
        using var reader = cmd.ExecuteReader(); while (reader.Read()) yield return (reader.GetString(0),reader.GetString(1),reader.GetString(2));
    }
    private void RequireComplete(string name) { if (Get("complete:" + name) != "1") throw new InvalidOperationException("완료되지 않은 스캔입니다: " + name); }
    private void Execute(string sql) { using var cmd = connection.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public void Dispose() { try { FlushOutcomes(); } finally { connection.Dispose(); } }
}
