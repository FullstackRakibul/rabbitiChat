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
        EnsureDocumentTables(db);
        EnsureExtractionTables(db);
        EnsureTodoRabbitTables(db);
        EnsureNotesTable(db);
        EnsureMessageWebSearchColumn(db);
        EnsureContextSourceTable(db);
        EnsureSearchHistoryTable(db);
        EnsureConversationActiveSourcesColumn(db);
        EnsureRbacSchema(db);
        EnsureIeltsTables(db);
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

    private static void EnsureDocumentTables(AppDbContext db)
    {
        if (!TableExists(db, "Documents"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""Documents"" (
                    ""Id""             INTEGER NOT NULL CONSTRAINT ""PK_Documents"" PRIMARY KEY AUTOINCREMENT,
                    ""OwnerUserId""    INTEGER NOT NULL,
                    ""FileName""       TEXT NOT NULL,
                    ""ContentType""    TEXT NOT NULL,
                    ""Buyer""          TEXT NULL,
                    ""Status""         TEXT NOT NULL,
                    ""Error""          TEXT NULL,
                    ""FilePath""       TEXT NULL,
                    ""ExtractedJson""  TEXT NULL,
                    ""ExtractedAt""    TEXT NULL,
                    ""ConversationId"" INTEGER NULL,
                    ""CreatedAt""      TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_Documents_OwnerUserId"" ON ""Documents"" (""OwnerUserId"");");
        }
        else
        {
            // Older db files predate the extraction columns.
            if (!ColumnExists(db, "Documents", "ExtractedJson"))
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""ExtractedJson"" TEXT NULL;");
            if (!ColumnExists(db, "Documents", "ExtractedAt"))
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""ExtractedAt"" TEXT NULL;");
            if (!ColumnExists(db, "Documents", "ConversationId"))
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""ConversationId"" INTEGER NULL;");
            if (!ColumnExists(db, "Documents", "FilePath"))
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""FilePath"" TEXT NULL;");
        }

        if (!TableExists(db, "EmbeddingChunks"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""EmbeddingChunks"" (
                    ""Id""          INTEGER NOT NULL CONSTRAINT ""PK_EmbeddingChunks"" PRIMARY KEY AUTOINCREMENT,
                    ""DocumentId""  INTEGER NOT NULL,
                    ""OwnerUserId"" INTEGER NULL,
                    ""Buyer""       TEXT NULL,
                    ""StyleNumber"" TEXT NULL,
                    ""SourceRef""   TEXT NULL,
                    ""Text""        TEXT NOT NULL,
                    ""Vector""      BLOB NOT NULL,
                    ""CreatedAt""   TEXT NOT NULL,
                    CONSTRAINT ""FK_EmbeddingChunks_Documents_DocumentId""
                        FOREIGN KEY (""DocumentId"") REFERENCES ""Documents"" (""Id"") ON DELETE CASCADE
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_EmbeddingChunks_DocumentId""  ON ""EmbeddingChunks"" (""DocumentId"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_EmbeddingChunks_OwnerUserId"" ON ""EmbeddingChunks"" (""OwnerUserId"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_EmbeddingChunks_Buyer""       ON ""EmbeddingChunks"" (""Buyer"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_EmbeddingChunks_StyleNumber"" ON ""EmbeddingChunks"" (""StyleNumber"");");
        }
    }

    /// <summary>
    /// Connected sources / buyer profiles. One table for both — Kind separates
    /// them — so the chat and workspace tabs share a single context mechanism.
    /// </summary>
    private static void EnsureContextSourceTable(AppDbContext db)
    {
        if (TableExists(db, "ContextSources")) return;

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE ""ContextSources"" (
                ""Id""          INTEGER NOT NULL CONSTRAINT ""PK_ContextSources"" PRIMARY KEY AUTOINCREMENT,
                ""OwnerUserId"" INTEGER NOT NULL,
                ""Name""        TEXT NOT NULL,
                ""Kind""        TEXT NOT NULL,
                ""Content""     TEXT NOT NULL,
                ""IsActive""    INTEGER NOT NULL,
                ""SortOrder""   INTEGER NOT NULL,
                ""CreatedAt""   TEXT NOT NULL,
                ""UpdatedAt""   TEXT NOT NULL
            );");
        db.Database.ExecuteSqlRaw(
            @"CREATE INDEX ""IX_ContextSources_OwnerUserId_Kind"" ON ""ContextSources"" (""OwnerUserId"", ""Kind"");");
    }

    /// <summary>
    /// Remembered search queries. Query text and usage counts only — result
    /// payloads are never persisted, so there is no column here to hold one.
    /// </summary>
    private static void EnsureSearchHistoryTable(AppDbContext db)
    {
        if (TableExists(db, "SearchHistories")) return;

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE ""SearchHistories"" (
                ""Id""          INTEGER NOT NULL CONSTRAINT ""PK_SearchHistories"" PRIMARY KEY AUTOINCREMENT,
                ""OwnerUserId"" INTEGER NOT NULL,
                ""Query""       TEXT NOT NULL,
                ""Title""       TEXT NULL,
                ""MaxResults""  INTEGER NOT NULL,
                ""RunCount""    INTEGER NOT NULL,
                ""LastRunAt""   TEXT NOT NULL,
                ""CreatedAt""   TEXT NOT NULL,
                ""UpdatedAt""   TEXT NOT NULL,
                ""IsDeleted""   INTEGER NOT NULL,
                ""DeletedAt""   TEXT NULL
            );");
        db.Database.ExecuteSqlRaw(
            @"CREATE INDEX ""IX_SearchHistories_OwnerUserId_IsDeleted_LastRunAt""
              ON ""SearchHistories"" (""OwnerUserId"", ""IsDeleted"", ""LastRunAt"");");
    }

    /// <summary>
    /// Which sources were switched on for a conversation, so reopening it restores
    /// the same context set. Null on older rows — read as "none recorded".
    /// </summary>
    private static void EnsureConversationActiveSourcesColumn(AppDbContext db)
    {
        if (ColumnExists(db, "Conversations", "ActiveSourceIds")) return;
        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Conversations"" ADD COLUMN ""ActiveSourceIds"" TEXT NULL;");
    }

    /// <summary>
    /// RBAC — the Role column and the tab-grant table.
    ///
    /// The column is added with DEFAULT 'General', which SQLite applies to every
    /// existing row in place: each pre-RBAC account becomes a General user with no
    /// grants, i.e. exactly the everyday tab set and nothing more. No user data is
    /// read or rewritten, so conversations, documents and boards are untouched.
    /// </summary>
    private static void EnsureRbacSchema(AppDbContext db)
    {
        if (TableExists(db, "Users") && !ColumnExists(db, "Users", "Role"))
        {
            db.Database.ExecuteSqlRaw(
                @"ALTER TABLE ""Users"" ADD COLUMN ""Role"" TEXT NOT NULL DEFAULT 'General';");
        }

        if (!TableExists(db, "UserTabPermissions"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""UserTabPermissions"" (
                    ""Id""        INTEGER NOT NULL CONSTRAINT ""PK_UserTabPermissions"" PRIMARY KEY AUTOINCREMENT,
                    ""UserId""    INTEGER NOT NULL,
                    ""TabKey""    TEXT NOT NULL,
                    ""CreatedAt"" TEXT NOT NULL,
                    CONSTRAINT ""FK_UserTabPermissions_Users_UserId""
                        FOREIGN KEY (""UserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
                );");
            db.Database.ExecuteSqlRaw(
                @"CREATE UNIQUE INDEX ""IX_UserTabPermissions_UserId_TabKey""
                  ON ""UserTabPermissions"" (""UserId"", ""TabKey"");");
        }
    }

    /// <summary>
    /// IELTS — the mistake spine and the Reading engine.
    ///
    /// Five tables, each guarded independently: a database that already has some
    /// of them (a partially applied earlier run) gains only what it is missing.
    /// </summary>
    private static void EnsureIeltsTables(AppDbContext db)
    {
        if (!TableExists(db, "Mistakes"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""Mistakes"" (
                    ""Id""            INTEGER NOT NULL CONSTRAINT ""PK_Mistakes"" PRIMARY KEY AUTOINCREMENT,
                    ""OwnerUserId""   INTEGER NOT NULL,
                    ""Module""        TEXT NOT NULL,
                    ""SourceType""    TEXT NOT NULL,
                    ""SourceId""      INTEGER NOT NULL,
                    ""QuestionType""  TEXT NOT NULL,
                    ""Skill""         TEXT NULL,
                    ""Topic""         TEXT NULL,
                    ""YourAnswer""    TEXT NOT NULL,
                    ""CorrectAnswer"" TEXT NOT NULL,
                    ""MistakeType""   TEXT NULL,
                    ""Severity""      INTEGER NOT NULL,
                    ""FirstSeenAt""   TEXT NOT NULL,
                    ""LastSeenAt""    TEXT NOT NULL,
                    ""TimesSeen""     INTEGER NOT NULL,
                    ""TimesResolved"" INTEGER NOT NULL,
                    ""NextReviewAt""  TEXT NULL,
                    ""Resolved""      INTEGER NOT NULL
                );");
            db.Database.ExecuteSqlRaw(
                @"CREATE INDEX ""IX_Mistakes_OwnerUserId_Module_NextReviewAt""
                  ON ""Mistakes"" (""OwnerUserId"", ""Module"", ""NextReviewAt"");");
        }

        if (!TableExists(db, "ReadingTests"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""ReadingTests"" (
                    ""Id""          INTEGER NOT NULL CONSTRAINT ""PK_ReadingTests"" PRIMARY KEY AUTOINCREMENT,
                    ""OwnerUserId"" INTEGER NOT NULL,
                    ""IsSystem""    INTEGER NOT NULL DEFAULT 0,
                    ""SeedKey""     TEXT NULL,
                    ""Title""       TEXT NOT NULL,
                    ""Passage""     TEXT NOT NULL,
                    ""Source""      TEXT NOT NULL,
                    ""CreatedAt""   TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(
                @"CREATE INDEX ""IX_ReadingTests_OwnerUserId"" ON ""ReadingTests"" (""OwnerUserId"");");
        }

        // Shared demo content. Both columns are additive, so an existing database
        // gains them in place: every pre-existing test stays user-authored
        // (IsSystem = 0, SeedKey NULL) and no row is read or rewritten.
        if (TableExists(db, "ReadingTests"))
        {
            if (!ColumnExists(db, "ReadingTests", "IsSystem"))
            {
                db.Database.ExecuteSqlRaw(
                    @"ALTER TABLE ""ReadingTests"" ADD COLUMN ""IsSystem"" INTEGER NOT NULL DEFAULT 0;");
            }

            if (!ColumnExists(db, "ReadingTests", "SeedKey"))
            {
                db.Database.ExecuteSqlRaw(
                    @"ALTER TABLE ""ReadingTests"" ADD COLUMN ""SeedKey"" TEXT NULL;");
            }

            // Partial index: user tests all leave SeedKey null and SQLite counts
            // each NULL as distinct, so only seeded rows are constrained. This is
            // what makes a re-seed an update rather than a duplicate.
            db.Database.ExecuteSqlRaw(
                @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ReadingTests_SeedKey""
                  ON ""ReadingTests"" (""SeedKey"") WHERE ""SeedKey"" IS NOT NULL;");
        }

        if (!TableExists(db, "ReadingQuestions"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""ReadingQuestions"" (
                    ""Id""            INTEGER NOT NULL CONSTRAINT ""PK_ReadingQuestions"" PRIMARY KEY AUTOINCREMENT,
                    ""ReadingTestId"" INTEGER NOT NULL,
                    ""Order""         INTEGER NOT NULL,
                    ""QuestionType""  TEXT NOT NULL,
                    ""Prompt""        TEXT NOT NULL,
                    ""OptionsJson""   TEXT NULL,
                    ""CorrectAnswer"" TEXT NOT NULL,
                    ""Skill""         TEXT NULL,
                    ""Topic""         TEXT NULL,
                    ""Difficulty""    TEXT NULL,
                    CONSTRAINT ""FK_ReadingQuestions_ReadingTests_ReadingTestId""
                        FOREIGN KEY (""ReadingTestId"") REFERENCES ""ReadingTests"" (""Id"") ON DELETE CASCADE
                );");
            db.Database.ExecuteSqlRaw(
                @"CREATE INDEX ""IX_ReadingQuestions_ReadingTestId"" ON ""ReadingQuestions"" (""ReadingTestId"");");
        }

        if (!TableExists(db, "ReadingAttempts"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""ReadingAttempts"" (
                    ""Id""              INTEGER NOT NULL CONSTRAINT ""PK_ReadingAttempts"" PRIMARY KEY AUTOINCREMENT,
                    ""OwnerUserId""     INTEGER NOT NULL,
                    ""ReadingTestId""   INTEGER NOT NULL,
                    ""StartedAt""       TEXT NOT NULL,
                    ""SubmittedAt""     TEXT NULL,
                    ""DurationSeconds"" INTEGER NOT NULL,
                    ""RawScore""        INTEGER NOT NULL,
                    ""TotalQuestions""  INTEGER NOT NULL,
                    ""BandEstimate""    REAL NULL
                );");
            db.Database.ExecuteSqlRaw(
                @"CREATE INDEX ""IX_ReadingAttempts_OwnerUserId_StartedAt""
                  ON ""ReadingAttempts"" (""OwnerUserId"", ""StartedAt"");");
        }

        if (!TableExists(db, "ReadingAttemptAnswers"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""ReadingAttemptAnswers"" (
                    ""Id""                INTEGER NOT NULL CONSTRAINT ""PK_ReadingAttemptAnswers"" PRIMARY KEY AUTOINCREMENT,
                    ""ReadingAttemptId""  INTEGER NOT NULL,
                    ""ReadingQuestionId"" INTEGER NOT NULL,
                    ""GivenAnswer""       TEXT NOT NULL,
                    ""IsCorrect""         INTEGER NOT NULL,
                    CONSTRAINT ""FK_ReadingAttemptAnswers_ReadingAttempts_ReadingAttemptId""
                        FOREIGN KEY (""ReadingAttemptId"") REFERENCES ""ReadingAttempts"" (""Id"") ON DELETE CASCADE
                );");
            db.Database.ExecuteSqlRaw(
                @"CREATE INDEX ""IX_ReadingAttemptAnswers_ReadingAttemptId""
                  ON ""ReadingAttemptAnswers"" (""ReadingAttemptId"");");
        }
    }

    private static void EnsureExtractionTables(AppDbContext db)
    {
        if (!TableExists(db, "BuyerProfiles"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""BuyerProfiles"" (
                    ""Id""               INTEGER NOT NULL CONSTRAINT ""PK_BuyerProfiles"" PRIMARY KEY AUTOINCREMENT,
                    ""Name""             TEXT NOT NULL,
                    ""DetectPattern""    TEXT NOT NULL,
                    ""ParserKey""        TEXT NOT NULL,
                    ""ChunkingStrategy"" TEXT NOT NULL,
                    ""IsActive""         INTEGER NOT NULL,
                    ""CreatedAt""        TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX ""IX_BuyerProfiles_Name"" ON ""BuyerProfiles"" (""Name"");");
        }

        if (!TableExists(db, "ExtractionRuns"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""ExtractionRuns"" (
                    ""Id""                INTEGER NOT NULL CONSTRAINT ""PK_ExtractionRuns"" PRIMARY KEY AUTOINCREMENT,
                    ""DocumentId""        INTEGER NOT NULL,
                    ""OwnerUserId""       INTEGER NOT NULL,
                    ""BuyerProfileId""    INTEGER NULL,
                    ""ParserKey""         TEXT NOT NULL,
                    ""Status""            TEXT NOT NULL,
                    ""ResultJson""        TEXT NOT NULL,
                    ""FailedChecks""      TEXT NOT NULL,
                    ""ConfirmedJson""     TEXT NULL,
                    ""ConfirmedByUserId"" INTEGER NULL,
                    ""ConfirmedAt""       TEXT NULL,
                    ""EditsJson""         TEXT NULL,
                    ""CreatedAt""         TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_ExtractionRuns_DocumentId"" ON ""ExtractionRuns"" (""DocumentId"");");
        }
        else
        {
            // Confirm columns arrived after the table itself.
            if (!ColumnExists(db, "ExtractionRuns", "ConfirmedJson"))
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""ConfirmedJson"" TEXT NULL;");
            if (!ColumnExists(db, "ExtractionRuns", "ConfirmedByUserId"))
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""ConfirmedByUserId"" INTEGER NULL;");
            if (!ColumnExists(db, "ExtractionRuns", "ConfirmedAt"))
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""ConfirmedAt"" TEXT NULL;");
            if (!ColumnExists(db, "ExtractionRuns", "EditsJson"))
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""EditsJson"" TEXT NULL;");
        }

        // Seed the one template that has a dedicated parser.
        var kohls = db.Database
            .SqlQueryRaw<int>(@"SELECT COUNT(*) AS ""Value"" FROM ""BuyerProfiles"" WHERE ""Name"" = 'Kohls';")
            .AsEnumerable().First();
        if (kohls == 0)
        {
            db.Database.ExecuteSqlRaw(@"
                INSERT INTO ""BuyerProfiles""
                    (""Name"", ""DetectPattern"", ""ParserKey"", ""ChunkingStrategy"", ""IsActive"", ""CreatedAt"")
                VALUES
                    ('Kohls', 'SPS Commerce|Kohls|Order #:|Vendors Style', 'KohlsSps', 'none', 1, {0});",
                DateTime.Now.ToString("o"));
        }
    }

    /// <summary>
    /// todorabbit — a creation of Mostisko, the AI.
    /// Kanban tables. Each is created only when absent, so an existing db file
    /// gains the board module without touching any other table's data.
    /// </summary>
    private static void EnsureTodoRabbitTables(AppDbContext db)
    {
        if (!TableExists(db, "Boards"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""Boards"" (
                    ""Id""          INTEGER NOT NULL CONSTRAINT ""PK_Boards"" PRIMARY KEY AUTOINCREMENT,
                    ""OwnerUserId"" INTEGER NOT NULL,
                    ""Name""        TEXT NOT NULL,
                    ""CreatedAt""   TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_Boards_OwnerUserId"" ON ""Boards"" (""OwnerUserId"");");
        }

        if (!TableExists(db, "Columns"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""Columns"" (
                    ""Id""        INTEGER NOT NULL CONSTRAINT ""PK_Columns"" PRIMARY KEY AUTOINCREMENT,
                    ""BoardId""   INTEGER NOT NULL,
                    ""Title""     TEXT NOT NULL,
                    ""SortOrder"" INTEGER NOT NULL,
                    ""ColorKey""  TEXT NOT NULL,
                    ""CreatedAt"" TEXT NOT NULL,
                    CONSTRAINT ""FK_Columns_Boards_BoardId""
                        FOREIGN KEY (""BoardId"") REFERENCES ""Boards"" (""Id"") ON DELETE CASCADE
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_Columns_BoardId"" ON ""Columns"" (""BoardId"");");
        }

        if (!TableExists(db, "Cards"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""Cards"" (
                    ""Id""        INTEGER NOT NULL CONSTRAINT ""PK_Cards"" PRIMARY KEY AUTOINCREMENT,
                    ""ColumnId""  INTEGER NOT NULL,
                    ""Title""     TEXT NOT NULL,
                    ""Content""   TEXT NOT NULL,
                    ""Priority""  TEXT NULL,
                    ""SortOrder"" INTEGER NOT NULL,
                    ""CreatedAt"" TEXT NOT NULL,
                    ""UpdatedAt"" TEXT NOT NULL,
                    CONSTRAINT ""FK_Cards_Columns_ColumnId""
                        FOREIGN KEY (""ColumnId"") REFERENCES ""Columns"" (""Id"") ON DELETE CASCADE
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_Cards_ColumnId"" ON ""Cards"" (""ColumnId"");");
        }
    }

    private static void EnsureNotesTable(AppDbContext db)
    {
        if (TableExists(db, "Notes")) return;

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE ""Notes"" (
                ""Id""          INTEGER NOT NULL CONSTRAINT ""PK_Notes"" PRIMARY KEY AUTOINCREMENT,
                ""OwnerUserId"" INTEGER NOT NULL,
                ""Content""     TEXT NOT NULL,
                ""Title""       TEXT NOT NULL,
                ""IsPinned""    INTEGER NOT NULL,
                ""FolderName""  TEXT NULL,
                ""IsShared""    INTEGER NOT NULL,
                ""ShareToken""  TEXT NULL,
                ""SharedAt""    TEXT NULL,
                ""CreatedAt""   TEXT NOT NULL,
                ""UpdatedAt""   TEXT NOT NULL,
                ""IsDeleted""   INTEGER NOT NULL,
                ""DeletedAt""   TEXT NULL,
                CONSTRAINT ""FK_Notes_Users_OwnerUserId""
                    FOREIGN KEY (""OwnerUserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
            );");
            
        db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_Notes_OwnerUserId_IsDeleted_IsPinned_UpdatedAt"" ON ""Notes"" (""OwnerUserId"", ""IsDeleted"", ""IsPinned"", ""UpdatedAt"");");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX ""IX_Notes_ShareToken"" ON ""Notes"" (""ShareToken"") WHERE ""ShareToken"" IS NOT NULL;");
    }

    /// <summary>Flags replies that were grounded on live web results. Older files predate it.</summary>
    private static void EnsureMessageWebSearchColumn(AppDbContext db)
    {
        if (!TableExists(db, "Messages")) return;
        if (ColumnExists(db, "Messages", "WebSearchUsed")) return;

        db.Database.ExecuteSqlRaw(
            @"ALTER TABLE ""Messages"" ADD COLUMN ""WebSearchUsed"" INTEGER NOT NULL DEFAULT 0;");
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
