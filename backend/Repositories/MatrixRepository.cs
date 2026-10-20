// ম্যাট্রিক্স — EF access for the Mx* entities, and the ONLY place a dataset table
// (mx_ds_<id>) is created, filled, read or changed (docs/MATRIX_PROMPT.md §3.2).
//
// Table names come from the dataset's id; column names from DatasetImporter's
// plan (catalog keys or col_<letter>), checked against a strict pattern before
// any SQL is built. Every value travels as a parameter. Nothing here takes a
// name from a header, a request or a model.

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RabbitIC.Api.Data;
using RabbitIC.Api.Matrix;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Repositories;

public class MatrixRepository : IMatrixRepository
{
    private readonly AppDbContext _db;

    public MatrixRepository(AppDbContext db) => _db = db;

    // ---- uploads, templates, datasets ----

    public async Task<MxUpload> AddUploadAsync(MxUpload upload, CancellationToken ct = default)
    {
        _db.MxUploads.Add(upload);
        await _db.SaveChangesAsync(ct);
        return upload;
    }

    public Task<MxUpload?> GetUploadAsync(int id, CancellationToken ct = default) =>
        _db.MxUploads.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<List<MxUpload>> ListUploadsAsync(int take, CancellationToken ct = default) =>
        _db.MxUploads.AsNoTracking().OrderByDescending(u => u.Id).Take(take).ToListAsync(ct);

    public Task<List<MxTemplate>> ListTemplatesAsync(CancellationToken ct = default) =>
        _db.MxTemplates.AsNoTracking().OrderBy(t => t.Name).ThenByDescending(t => t.Version).ToListAsync(ct);

    public Task<MxTemplate?> GetTemplateAsync(int id, CancellationToken ct = default) =>
        _db.MxTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<MxTemplate> AddTemplateAsync(MxTemplate template, CancellationToken ct = default)
    {
        _db.MxTemplates.Add(template);
        await _db.SaveChangesAsync(ct);
        return template;
    }

    public Task<List<MxDataset>> ListDatasetsAsync(CancellationToken ct = default) =>
        _db.MxDatasets.AsNoTracking().OrderByDescending(d => d.Id).ToListAsync(ct);

    public Task<MxDataset?> GetDatasetAsync(int id, CancellationToken ct = default) =>
        _db.MxDatasets.FirstOrDefaultAsync(d => d.Id == id, ct);

    // ---- dataset tables ----

    private static string Q(string name)
    {
        if (!DatasetImporter.SafeName.IsMatch(name)) throw new InvalidOperationException($"Unsafe SQL name '{name}'.");
        return "\"" + name + "\"";
    }

    private static string TableOf(MxDataset d) => Q(d.TableName);

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<MxDataset> CreateDatasetAsync(MxDataset dataset, IReadOnlyList<MxDsColumn> columns,
        IReadOnlyList<object?[]> rows, IReadOnlyList<int> sourceRows, CancellationToken ct = default)
    {
        var conn = await OpenAsync(ct);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        _db.MxDatasets.Add(dataset);
        await _db.SaveChangesAsync(ct);
        dataset.TableName = $"mx_ds_{dataset.Id}";
        await _db.SaveChangesAsync(ct);

        var table = TableOf(dataset);
        var defs = string.Join(", ", columns.Select(c => $"{Q(c.Key)} {DatasetImporter.SqlType(c.Type)}"));
        await ExecAsync(conn, $"CREATE TABLE {table} (\"_row\" INTEGER NOT NULL, \"_sheet\" TEXT NOT NULL{(defs.Length > 0 ? ", " + defs : "")})", ct);

        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx.GetDbTransaction();
            var names = new[] { "_row", "_sheet" }.Concat(columns.Select(c => c.Key)).ToList();
            insert.CommandText = $"INSERT INTO {table} ({string.Join(", ", names.Select(Q))}) VALUES ({string.Join(", ", names.Select((_, i) => "$p" + i))})";
            var ps = names.Select((_, i) => { var p = insert.CreateParameter(); p.ParameterName = "$p" + i; insert.Parameters.Add(p); return p; }).ToList();
            insert.Prepare();
            for (var r = 0; r < rows.Count; r++)
            {
                ps[0].Value = sourceRows[r];
                ps[1].Value = dataset.Sheet;
                for (var c = 0; c < columns.Count; c++) ps[c + 2].Value = rows[r][c] ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await ExecAsync(conn, $"CREATE INDEX {Q($"ix_{dataset.TableName}_row")} ON {table} (\"_row\")", ct);
        foreach (var c in columns.Where(c => FieldCatalog.Find(c.Field)?.Role is MxRole.Dimension or MxRole.Date || c.Key == "emp_id"))
            await ExecAsync(conn, $"CREATE INDEX {Q($"ix_{dataset.TableName}_{c.Key}"[..Math.Min(63, $"ix_{dataset.TableName}_{c.Key}".Length)])} ON {table} ({Q(c.Key)})", ct);

        await tx.CommitAsync(ct);
        return dataset;

        async Task ExecAsync(DbConnection cn, string sql, CancellationToken token)
        {
            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx.GetDbTransaction();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(token);
        }
    }

    public async Task SetCurrentAsync(MxDataset dataset, CancellationToken ct = default)
    {
        var same = await _db.MxDatasets.Where(d => d.ReportKind == dataset.ReportKind && d.TemplateId == dataset.TemplateId).ToListAsync(ct);
        var latest = same.OrderBy(d => d.ReportDate ?? "").ThenBy(d => d.Id).Last();
        foreach (var d in same) d.IsCurrent = d.Id == latest.Id;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(int Total, List<Dictionary<string, object?>> Rows)> RowsAsync(MxDataset dataset, IReadOnlyList<string> keys,
        IReadOnlyList<MxRowFilter> filters, IReadOnlyList<int>? onlyRows, int offset, int limit, CancellationToken ct = default)
    {
        var conn = await OpenAsync(ct);
        var where = new List<string>();
        var args = new List<object>();
        string P(object v) { args.Add(v); return "$a" + (args.Count - 1); }

        foreach (var f in filters)
        {
            var col = Q(f.Key);
            switch (f.Op)
            {
                case "=": where.Add($"{col} = {P(f.Values.FirstOrDefault() ?? "")}"); break;
                case "contains": where.Add($"instr(lower(CAST({col} AS TEXT)), lower({P(f.Values.FirstOrDefault() ?? "")})) > 0"); break;
                case "in": where.Add($"{col} IN ({string.Join(", ", f.Values.Select(v => P(v)))})"); break;
                case "empty": where.Add($"({col} IS NULL OR {col} = '')"); break;
                default: throw new ArgumentException($"Unknown filter operator '{f.Op}'.");
            }
        }
        if (onlyRows is not null) where.Add(onlyRows.Count == 0 ? "0" : $"\"_row\" IN ({string.Join(", ", onlyRows.Select(r => P(r)))})");
        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";

        await using var count = conn.CreateCommand();
        count.CommandText = $"SELECT count(*) FROM {TableOf(dataset)}{whereSql}";
        Bind(count, args);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));

        await using var cmd = conn.CreateCommand();
        var select = string.Join(", ", new[] { "_row" }.Concat(keys).Select(Q));
        cmd.CommandText = $"SELECT {select} FROM {TableOf(dataset)}{whereSql} ORDER BY \"_row\" LIMIT {P(limit)} OFFSET {P(offset)}";
        Bind(cmd, args);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(keys.Count + 1);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return (total, rows);
    }

    private static void Bind(DbCommand cmd, List<object> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "$a" + i;
            p.Value = args[i];
            cmd.Parameters.Add(p);
        }
    }

    public async Task<HashSet<string>> DistinctAsync(MxDataset dataset, string key, CancellationToken ct = default)
    {
        var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT lower(trim({Q(key)})) FROM {TableOf(dataset)} WHERE {Q(key)} IS NOT NULL";
        var set = new HashSet<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) if (!reader.IsDBNull(0)) set.Add(reader.GetString(0));
        return set;
    }

    // ---- quality ----

    public async Task AddIssuesAsync(IEnumerable<MxQualityIssue> issues, CancellationToken ct = default)
    {
        _db.MxQualityIssues.AddRange(issues);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<MxQualityIssue>> ListIssuesAsync(int datasetId, CancellationToken ct = default) =>
        _db.MxQualityIssues.AsNoTracking().Where(i => i.DatasetId == datasetId).OrderBy(i => i.Id).ToListAsync(ct);

    public Task<MxQualityIssue?> GetIssueAsync(int id, CancellationToken ct = default) =>
        _db.MxQualityIssues.FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task ReplaceIssuesAsync(int datasetId, string check, MxQualityIssue? issue, CancellationToken ct = default)
    {
        _db.MxQualityIssues.RemoveRange(await _db.MxQualityIssues.Where(i => i.DatasetId == datasetId && i.Check == check).ToListAsync(ct));
        if (issue is not null)
        {
            issue.DatasetId = datasetId;
            _db.MxQualityIssues.Add(issue);
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> ApplyFixAsync(MxDataset dataset, string key, string kind, CancellationToken ct = default)
    {
        var conn = await OpenAsync(ct);
        var table = TableOf(dataset);
        var col = Q(key);
        var raw = Q("_raw_" + key);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        async Task<int> Exec(string sql, params object[] args)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx.GetDbTransaction();
            cmd.CommandText = sql;
            Bind(cmd, args.ToList());
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        // The raw values first, once: a second fix on the same column keeps the original.
        var hasRaw = false;
        await using (var info = conn.CreateCommand())
        {
            info.Transaction = tx.GetDbTransaction();
            info.CommandText = $"SELECT count(*) FROM pragma_table_info('{dataset.TableName}') WHERE name = $a0";
            Bind(info, new List<object> { "_raw_" + key });
            hasRaw = Convert.ToInt32(await info.ExecuteScalarAsync(ct)) > 0;
        }
        if (!hasRaw)
        {
            await Exec($"ALTER TABLE {table} ADD COLUMN {raw} TEXT");
            await Exec($"UPDATE {table} SET {raw} = {col}");
        }

        var changed = 0;
        switch (kind)
        {
            case MxCheck.FixTrim:
                changed = await Exec($"UPDATE {table} SET {col} = trim({col}) WHERE {col} <> trim({col})");
                break;
            case MxCheck.FixPadIds:
                changed = await Exec($"UPDATE {table} SET {col} = substr('000000' || {col}, -6) WHERE length({col}) < 6 AND {col} NOT GLOB '*[^0-9]*'");
                break;
            case MxCheck.FixUnify:
            {
                // Each group of spellings that normalise alike takes its most common spelling.
                var counts = new List<(string Value, long Count)>();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx.GetDbTransaction();
                    cmd.CommandText = $"SELECT trim({col}), count(*) FROM {table} WHERE {col} IS NOT NULL GROUP BY trim({col})";
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) counts.Add((reader.GetString(0), reader.GetInt64(1)));
                }
                foreach (var group in counts.GroupBy(c => FieldCatalog.Normalise(c.Value)).Where(g => g.Count() > 1))
                {
                    var keep = group.OrderByDescending(c => c.Count).First().Value;
                    foreach (var other in group.Where(c => c.Value != keep))
                        changed += await Exec($"UPDATE {table} SET {col} = $a0 WHERE trim({col}) = $a1", keep, other.Value);
                }
                break;
            }
            default:
                throw new ArgumentException($"Unknown fix '{kind}'.");
        }

        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task AddFixLogAsync(MxFixLog log, CancellationToken ct = default)
    {
        _db.MxFixLogs.Add(log);
        await _db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
