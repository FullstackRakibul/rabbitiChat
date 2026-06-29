using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace RabbitIC.Api.Data;

/// <summary>
/// One-time, idempotent schema upgrade for db files created before per-user accounts existed.
///
/// This project uses EnsureCreated() rather than EF migrations, so EnsureCreated() will create
/// a fully-correct schema for a brand-new file but will NOT alter an existing one. This bridges
/// that gap for older files: it adds the Users table and Conversations.UserId column if they're
/// missing, then assigns any ownerless conversations to a default account so existing chats are
/// preserved. Safe to run on every startup — each step checks first and no-ops if already applied.
/// </summary>
public static class SchemaUpgrade
{
    // Default account that inherits pre-auth conversations. Log in with these to see old chats.
    private const string LegacyUsername = "legacy";
    private const string LegacySecret = "legacy";

    public static void Run(AppDbContext db)
    {
        if (!TableExists(db, "Conversations")) return; // brand-new db; EnsureCreated already did it all

        EnsureUsersTable(db);
        var addedUserId = EnsureConversationUserIdColumn(db);

        // Only adopt orphans when we just introduced the column (i.e. an old file with existing rows).
        if (addedUserId)
            AdoptOrphanConversations(db);
    }

    private static bool TableExists(AppDbContext db, string name)
    {
        var conn = db.Database.GetDbConnection();
        OpenIfNeeded(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$n;";
        var p = cmd.CreateParameter(); p.ParameterName = "$n"; p.Value = name; cmd.Parameters.Add(p);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(AppDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        OpenIfNeeded(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void EnsureUsersTable(AppDbContext db)
    {
        if (TableExists(db, "Users")) return;

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE ""Users"" (
                ""Id""         INTEGER NOT NULL CONSTRAINT ""PK_Users"" PRIMARY KEY AUTOINCREMENT,
                ""Username""   TEXT NOT NULL,
                ""SecretHash"" TEXT NOT NULL,
                ""SecretSalt"" TEXT NOT NULL,
                ""Token""      TEXT NOT NULL,
                ""CreatedAt""  TEXT NOT NULL
            );");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX ""IX_Users_Username"" ON ""Users"" (""Username"");");
        db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_Users_Token"" ON ""Users"" (""Token"");");
    }

    private static bool EnsureConversationUserIdColumn(AppDbContext db)
    {
        if (ColumnExists(db, "Conversations", "UserId")) return false;
        // SQLite can't add a NOT NULL column without a default; backfill with 0, then adopt below.
        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Conversations"" ADD COLUMN ""UserId"" INTEGER NOT NULL DEFAULT 0;");
        return true;
    }

    private static void AdoptOrphanConversations(AppDbContext db)
    {
        var orphanCount = db.Database
            .SqlQueryRaw<int>(@"SELECT COUNT(*) AS ""Value"" FROM ""Conversations"" WHERE ""UserId"" = 0;")
            .AsEnumerable().First();
        if (orphanCount == 0) return;

        var legacyId = EnsureLegacyUser(db);
        db.Database.ExecuteSqlRaw(
            @"UPDATE ""Conversations"" SET ""UserId"" = {0} WHERE ""UserId"" = 0;", legacyId);
    }

    private static int EnsureLegacyUser(AppDbContext db)
    {
        var existing = db.Users.FirstOrDefault(u => u.Username == LegacyUsername);
        if (existing is not null) return existing.Id;

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(LegacySecret), salt, 100_000, HashAlgorithmName.SHA256, 32);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var user = new Models.User
        {
            Username = LegacyUsername,
            SecretHash = Convert.ToBase64String(hash),
            SecretSalt = Convert.ToBase64String(salt),
            Token = token,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private static void OpenIfNeeded(System.Data.Common.DbConnection conn)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
    }
}
