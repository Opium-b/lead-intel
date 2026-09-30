using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LeadIntel.Data;

public static class Json
{
    /// <summary>One set of JSON rules for the database (JSONB) and the API: snake_case keys, like the Python version.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // keep "→" and quotes readable in stored JSON
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>A JSON value as text: strings unquoted, numbers/bools as written, null/missing as null.</summary>
    public static string? Str(this IReadOnlyDictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) ? v.AsString() : null;

    public static string? AsString(this JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => v.GetRawText(),
    };

    /// <summary>Text of a boxed value that may be a CLR object or a JsonElement read back from JSONB.</summary>
    public static string? Text(object? value) => value switch
    {
        null => null,
        JsonElement e => e.AsString(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<SourceRecord> SourceRecords => Set<SourceRecord>();
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadEvent> LeadEvents => Set<LeadEvent>();
    public DbSet<ScoringRule> ScoringRules => Set<ScoringRule>();
    public DbSet<CollectorRun> CollectorRuns => Set<CollectorRun>();

    public static NpgsqlDataSource BuildDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.EnableDynamicJson().ConfigureJsonOptions(Json.Options);
        return builder.Build();
    }

    public static void Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options.UseNpgsql(dataSource).UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Columns the database fills in (DEFAULT now()): EF leaves them out of INSERTs
        b.Entity<Company>(e =>
        {
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Lead).WithOne(x => x.Company).HasForeignKey<Lead>(x => x.CompanyId);
        });
        b.Entity<Lead>(e =>
        {
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });
        b.Entity<Signal>().Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Entity<Contact>().Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Entity<LeadEvent>().Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Entity<SourceRecord>().Property(x => x.FetchedAt).HasDefaultValueSql("now()");
        b.Entity<ScoringRule>().Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        b.Entity<CollectorRun>().Property(x => x.StartedAt).HasDefaultValueSql("now()");
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;  // updated_at on every change, like SQLAlchemy's onupdate
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State == EntityState.Modified))
            switch (entry.Entity)
            {
                case Company c: c.UpdatedAt = now; break;
                case Lead l: l.UpdatedAt = now; break;
                case ScoringRule r: r.UpdatedAt = now; break;
            }
        return base.SaveChangesAsync(ct);
    }
}

public static class Schema
{
    /// <summary>Creates the tables on an empty database (Data/schema.sql, the same schema the Python version used).
    /// An existing database is left untouched.</summary>
    public static async Task EnsureCreatedAsync(AppDbContext db)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using (var check = new NpgsqlCommand("SELECT to_regclass('public.companies') IS NOT NULL", conn))
            if ((bool)(await check.ExecuteScalarAsync())!) return;
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql"));
        await using var create = new NpgsqlCommand(sql, conn);
        await create.ExecuteNonQueryAsync();
    }
}
