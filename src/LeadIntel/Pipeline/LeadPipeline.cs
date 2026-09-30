using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeadIntel.Collectors;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Scoring;
using LeadIntel.Signals;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LeadIntel.Pipeline;

/// <summary>Collector output -> companies/facts -> signals -> score -> lead. Every step is an idempotent upsert.</summary>
public class LeadPipeline(AppDbContext db, Settings settings, ILogger<LeadPipeline> log)
{
    public const int ExternalIdMax = 128;  // source_records.external_id
    const int BatchRows = 1000;
    static readonly string[] TrackedCensus = ["power_units", "total_drivers", "status_code"];

    /// <summary>Ids built from source fields can exceed the column: keep a readable prefix and make the rest a hash, so
    /// distinct long ids stay distinct and the same id always maps the same way.</summary>
    public static string FitExternalId(string v) =>
        v.Length <= ExternalIdMax ? v : $"{v[..(ExternalIdMax - 41)]}#{Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(v)))}";

    async Task<NpgsqlConnection> ConnAsync()
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        return conn;
    }

    /// <summary>Diff incoming census rows against the stored snapshot: each changed tracked field becomes a dated fact.
    /// The census row itself is overwritten on upsert, so this is the only place the old value is still visible.</summary>
    public async Task<List<EventRecord>> CensusChangesAsync(NormalizedBatch batch)
    {
        var incoming = batch.Events.Where(e => e.RecordType == "census").GroupBy(e => e.ExternalId).ToDictionary(g => g.Key, g => g.Last());
        if (incoming.Count == 0) return [];
        var keys = incoming.Keys.ToList();
        var stored = await db.SourceRecords.AsNoTracking()
            .Where(r => r.Source == FmcsaCollector.Name && r.RecordType == "census" && keys.Contains(r.ExternalId))
            .Select(r => new { r.ExternalId, r.Payload }).ToListAsync();
        var changes = new List<EventRecord>();
        foreach (var old in stored)
        {
            var now = incoming[old.ExternalId];
            foreach (var field in TrackedCensus)
            {
                string? before = old.Payload.Str(field), after = now.Payload.Str(field);
                if (before is null || after is null || before == after) continue;
                var payload = new Dictionary<string, JsonElement>
                {
                    ["field"] = JsonSerializer.SerializeToElement(field),
                    ["from"] = JsonSerializer.SerializeToElement(before),
                    ["to"] = JsonSerializer.SerializeToElement(after),
                    ["mcs150_date"] = JsonSerializer.SerializeToElement(now.Payload.Str("mcs150_date")),
                };
                changes.Add(new(now.Source, "census_change", $"{old.ExternalId}:{field}:{before}->{after}", old.ExternalId,
                                now.ObservedAt, now.SourceUrl, payload));
            }
        }
        return changes;
    }

    /// <summary>Upsert companies, contacts and raw facts. Returns ids of touched companies.</summary>
    public async Task<List<int>> IngestAsync(NormalizedBatch batch)
    {
        batch.Events = [.. batch.Events, .. await CensusChangesAsync(batch)];
        var conn = await ConnAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var ids = new Dictionary<string, int>();
        foreach (var c in batch.Companies)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO companies (dot_number, mc_number, name, dba_name, state, city, location, fleet_size, drivers,
                                       operating_status, added_at, attributes)
                VALUES (@dot, @mc, @name, @dba, @state, @city, @location, @fleet, @drivers, @status, @added, @attributes)
                ON CONFLICT (dot_number) DO UPDATE SET
                    name = EXCLUDED.name, dba_name = EXCLUDED.dba_name, state = EXCLUDED.state, city = EXCLUDED.city,
                    location = EXCLUDED.location, fleet_size = EXCLUDED.fleet_size, drivers = EXCLUDED.drivers,
                    operating_status = EXCLUDED.operating_status, added_at = EXCLUDED.added_at, attributes = EXCLUDED.attributes,
                    mc_number = COALESCE(EXCLUDED.mc_number, companies.mc_number), updated_at = now()
                RETURNING id
                """, conn, tx);
            cmd.Parameters.AddWithValue("dot", c.DotNumber);
            cmd.Parameters.AddWithValue("mc", (object?)c.McNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("name", c.Name);
            cmd.Parameters.AddWithValue("dba", (object?)c.DbaName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("state", (object?)c.State ?? DBNull.Value);
            cmd.Parameters.AddWithValue("city", (object?)c.City ?? DBNull.Value);
            cmd.Parameters.AddWithValue("location", (object?)c.Location ?? DBNull.Value);
            cmd.Parameters.AddWithValue("fleet", (object?)c.FleetSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("drivers", (object?)c.Drivers ?? DBNull.Value);
            cmd.Parameters.AddWithValue("status", (object?)c.OperatingStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("added", (object?)c.AddedAt ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Jsonb) { Value = Json.Serialize(c.Attributes) });
            ids[c.DotNumber] = (int)(await cmd.ExecuteScalarAsync())!;

            if (c.Contacts.Count > 0)
                await InsertContactsAsync(conn, tx, ids[c.DotNumber], c.Contacts);
        }

        // one row per (source, type, id): the last copy wins, like the Python dict comprehension
        var rows = batch.Events.Where(e => ids.ContainsKey(e.CompanyDot))
            .Select(e => (Event: e, Ext: FitExternalId(e.ExternalId)))
            .GroupBy(x => (x.Event.Source, x.Event.RecordType, x.Ext)).Select(g => g.Last()).ToList();
        foreach (var part in rows.Chunk(BatchRows))
        {
            var sql = new StringBuilder("INSERT INTO source_records (source, record_type, external_id, company_id, observed_at, source_url, payload, payload_hash) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (var i = 0; i < part.Length; i++)
            {
                var (e, ext) = part[i];
                sql.Append(i == 0 ? "" : ",").Append($"(@s{i}, @t{i}, @x{i}, @c{i}, @o{i}, @u{i}, @p{i}, @h{i})");
                cmd.Parameters.AddWithValue($"s{i}", e.Source);
                cmd.Parameters.AddWithValue($"t{i}", e.RecordType);
                cmd.Parameters.AddWithValue($"x{i}", ext);
                cmd.Parameters.AddWithValue($"c{i}", ids[e.CompanyDot]);
                cmd.Parameters.AddWithValue($"o{i}", (object?)e.ObservedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"u{i}", e.SourceUrl);
                cmd.Parameters.Add(new NpgsqlParameter($"p{i}", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(e.Payload) });
                cmd.Parameters.AddWithValue($"h{i}", PayloadHash.Of(e.Payload));
            }
            sql.Append("""
                 ON CONFLICT (source, record_type, external_id) DO UPDATE SET
                    company_id = EXCLUDED.company_id, observed_at = EXCLUDED.observed_at, source_url = EXCLUDED.source_url,
                    payload = EXCLUDED.payload, payload_hash = EXCLUDED.payload_hash, fetched_at = now()
                 WHERE source_records.payload_hash <> EXCLUDED.payload_hash
                """);
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return ids.Values.ToList();
    }

    public static async Task<List<string>> InsertContactsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int companyId,
                                                               IReadOnlyList<ContactRecord> contacts)
    {
        var sql = new StringBuilder("INSERT INTO contacts (company_id, type, value, label, source, source_url) VALUES ");
        await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
        cmd.Parameters.AddWithValue("company", companyId);
        for (var i = 0; i < contacts.Count; i++)
        {
            var ct = contacts[i];
            sql.Append(i == 0 ? "" : ",").Append($"(@company, @t{i}, @v{i}, @l{i}, @s{i}, @u{i})");
            cmd.Parameters.AddWithValue($"t{i}", ct.Type);
            cmd.Parameters.AddWithValue($"v{i}", ct.Value);
            cmd.Parameters.AddWithValue($"l{i}", (object?)ct.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"s{i}", ct.Source);
            cmd.Parameters.AddWithValue($"u{i}", (object?)ct.SourceUrl ?? DBNull.Value);
        }
        cmd.CommandText = sql + " ON CONFLICT (company_id, type, value) DO NOTHING RETURNING type";
        var inserted = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) inserted.Add(reader.GetString(0));
        return inserted;
    }

    /// <summary>Re-derive signals from stored facts, re-score, create/update the lead.</summary>
    public async Task<Lead?> ProcessCompanyAsync(int companyId, List<ScoringRule> rules, DateOnly today)
    {
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId);
        var facts = (await db.SourceRecords.AsNoTracking().Where(r => r.CompanyId == companyId).OrderBy(r => r.Id).ToListAsync())
            .Select(r => new Fact(r.Id, r.Source, r.RecordType, r.ExternalId, r.ObservedAt, r.SourceUrl, r.Payload)).ToList();
        var drafts = new Dictionary<string, SignalDraft>();
        foreach (var d in SignalEngine.Detect(facts, today)) drafts[d.DedupeKey] = d;  // last wins

        var existing = (await db.Signals.Where(s => s.CompanyId == companyId && s.Origin == "rule").Select(s => s.DedupeKey).ToListAsync()).ToHashSet();
        var conn = await ConnAsync();
        foreach (var part in drafts.Values.Chunk(BatchRows))
        {
            var sql = new StringBuilder("INSERT INTO signals (company_id, origin, type, description, severity, source, source_url, observed_at, detected_by, evidence, source_record_id, dedupe_key) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn };
            cmd.Parameters.AddWithValue("company", companyId);
            for (var i = 0; i < part.Length; i++)
            {
                var d = part[i];
                sql.Append(i == 0 ? "" : ",").Append($"(@company, 'rule', @t{i}, @d{i}, @sev{i}, @src{i}, @u{i}, @o{i}, @by{i}, @e{i}, @r{i}, @k{i})");
                cmd.Parameters.AddWithValue($"t{i}", d.Type);
                cmd.Parameters.AddWithValue($"d{i}", d.Description);
                cmd.Parameters.AddWithValue($"sev{i}", d.Severity);
                cmd.Parameters.AddWithValue($"src{i}", d.Source);
                cmd.Parameters.AddWithValue($"u{i}", (object?)d.SourceUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"o{i}", (object?)d.ObservedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"by{i}", d.DetectedBy);
                cmd.Parameters.Add(new NpgsqlParameter($"e{i}", NpgsqlDbType.Jsonb) { Value = Json.Serialize(d.Evidence) });
                cmd.Parameters.AddWithValue($"r{i}", (object?)d.SourceRecordId ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"k{i}", d.DedupeKey);
            }
            cmd.CommandText = sql + """
                 ON CONFLICT (company_id, dedupe_key) DO UPDATE SET
                    type = EXCLUDED.type, description = EXCLUDED.description, severity = EXCLUDED.severity, source = EXCLUDED.source,
                    source_url = EXCLUDED.source_url, observed_at = EXCLUDED.observed_at, detected_by = EXCLUDED.detected_by,
                    evidence = EXCLUDED.evidence, source_record_id = EXCLUDED.source_record_id
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        // Rule signals are a pure function of facts: drop ones the rules no longer produce (e.g. aged out).
        var keep = drafts.Keys.ToList();
        await db.Signals.Where(s => s.CompanyId == companyId && s.Origin == "rule" && !keep.Contains(s.DedupeKey)).ExecuteDeleteAsync();
        var added = drafts.Where(kv => !existing.Contains(kv.Key)).Select(kv => kv.Value).ToList();

        var contactTypes = (await db.Contacts.Where(c => c.CompanyId == companyId).Select(c => c.Type).ToListAsync()).ToHashSet();
        var lines = ServicesCatalog.Rank(drafts.Values.Select(d => (d.Type, d.Severity, (IReadOnlyDictionary<string, object?>)d.Evidence)))
                                   .Select(p => p.Key).ToList();
        var view = new CompanyView(company.OperatingStatus, company.FleetSize,
                                   drafts.Values.Select(d => new SignalView(d.Type, d.ObservedAt, d.Severity)).ToList(), contactTypes, lines);
        var (points, breakdown) = ScoringEngine.Score(view, rules, today);

        var now = DateTime.UtcNow;
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.CompanyId == companyId);
        if (lead is null)
        {
            if (drafts.Count == 0 || points < settings.LeadMinScore) return null;
            lead = new Lead { CompanyId = companyId, Score = points, ScoreBreakdown = breakdown, ServiceLines = lines, ScoredAt = now };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            db.LeadEvents.Add(new LeadEvent
            {
                LeadId = lead.Id, EventType = "CREATED",
                Meta = new() { ["score"] = points, ["signals"] = drafts.Values.Select(d => d.Type).Distinct().Order(StringComparer.Ordinal).ToList() },
            });
        }
        else
        {
            if (added.Count > 0)
                db.LeadEvents.Add(new LeadEvent
                {
                    LeadId = lead.Id, EventType = "SIGNALS_ADDED",
                    Meta = new()
                    {
                        ["signals"] = added.Take(20).Select(d => new Dictionary<string, string> { ["type"] = d.Type, ["description"] = d.Description }).ToList(),
                        ["count"] = added.Count,
                    },
                });
            if (lead.Score != points)
                db.LeadEvents.Add(new LeadEvent { LeadId = lead.Id, EventType = "SCORE_CHANGED", Meta = new() { ["from"] = lead.Score, ["to"] = points } });
            (lead.Score, lead.ScoreBreakdown, lead.ServiceLines, lead.ScoredAt) = (points, breakdown, lines, now);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();  // one company at a time: don't let tracked entities pile up over a long run
        return lead;
    }

    /// <summary>Process the given companies, or every company when <paramref name="companyIds"/> is null.</summary>
    public async Task<Dictionary<string, object?>> ProcessCompaniesAsync(List<int>? companyIds = null, DateOnly? today = null)
    {
        await ScoringEngine.SeedRulesAsync(db);
        var rules = await ScoringEngine.LoadRulesAsync(db);
        var day = today ?? DateOnly.FromDateTime(DateTime.Today);
        var ids = companyIds ?? await db.Companies.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync();
        // ponytail: one company per transaction, sequential; batch or parallelize past ~10k companies per run
        var leads = 0;
        foreach (var id in ids)
            if (await ProcessCompanyAsync(id, rules, day) is not null) leads++;
        log.LogInformation("processed {Count} companies, {Leads} leads", ids.Count, leads);
        return new() { ["processed"] = companyIds?.Count, ["leads"] = leads };
    }
}
