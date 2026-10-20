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
        EnsureObservabilityTables(db);
        EnsureMessageWebSearchColumn(db);
        EnsureContextSourceTable(db);
        EnsureHrLibrarySchema(db);
        EnsureHrRulebookSchema(db);
        EnsureHrProcedureSchema(db);
        EnsureSearchHistoryTable(db);
        EnsureConversationActiveSourcesColumn(db);
        EnsureRbacSchema(db);
        EnsureAccessSchema(db);
        EnsureTablesFromModel(db, "Mx");
        EnsureRoleModelPolicyTable(db);
        EnsureIeltsTables(db);
        EnsureOddityTables(db);
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
    /// The HR library (docs/HR_MODULE_PROMPT.md §3.2), for a database that predates it.
    ///
    /// ContextSources gains the HR columns. EmbeddingChunks is rebuilt once:
    /// DocumentId becomes nullable and the chunk gains ContextSourceId and Kind,
    /// so HR library chunks live in the same table and the same vector index as
    /// document chunks rather than in a second store. SQLite cannot relax a NOT
    /// NULL in place, hence the copy; it carries over whichever columns the old
    /// table has, and it runs only while DocumentId is still NOT NULL.
    /// </summary>
    private static void EnsureHrLibrarySchema(AppDbContext db)
    {
        AddColumnIfMissing(db, "ContextSources", "HrType", @"ALTER TABLE ""ContextSources"" ADD COLUMN ""HrType"" TEXT NULL;");
        AddColumnIfMissing(db, "ContextSources", "Mode", @"ALTER TABLE ""ContextSources"" ADD COLUMN ""Mode"" TEXT NULL;");
        AddColumnIfMissing(db, "ContextSources", "Version", @"ALTER TABLE ""ContextSources"" ADD COLUMN ""Version"" INTEGER NOT NULL DEFAULT 1;");
        AddColumnIfMissing(db, "ContextSources", "ApprovedBy", @"ALTER TABLE ""ContextSources"" ADD COLUMN ""ApprovedBy"" TEXT NULL;");
        AddColumnIfMissing(db, "ContextSources", "ApprovedAt", @"ALTER TABLE ""ContextSources"" ADD COLUMN ""ApprovedAt"" TEXT NULL;");

        // Phase 2: indexing state, HR conversations, citation lines.
        AddColumnIfMissing(db, "ContextSources", "IndexError", @"ALTER TABLE ""ContextSources"" ADD COLUMN ""IndexError"" TEXT NULL;");
        AddColumnIfMissing(db, "Conversations", "Kind", @"ALTER TABLE ""Conversations"" ADD COLUMN ""Kind"" TEXT NULL;");
        AddColumnIfMissing(db, "Messages", "HrMetaJson", @"ALTER TABLE ""Messages"" ADD COLUMN ""HrMetaJson"" TEXT NULL;");
        // Phase 2 decision: smaller chunks; a library split the old way is re-split once.
        AddColumnIfMissing(db, "ContextSources", "SplitVersion", @"ALTER TABLE ""ContextSources"" ADD COLUMN ""SplitVersion"" INTEGER NULL;");

        if (!ColumnIsNotNull(db, "EmbeddingChunks", "DocumentId"))
        {
            AddChunkLineColumns(db);
            return;
        }

        var carried = new[] { "Id", "DocumentId", "OwnerUserId", "Buyer", "StyleNumber", "SourceRef", "Text", "Vector", "CreatedAt" }
            .Where(c => ColumnExists(db, "EmbeddingChunks", c))
            .Select(c => $@"""{c}""");
        var columns = string.Join(", ", carried);

        // Nothing references EmbeddingChunks, but foreign keys are switched off
        // for the copy anyway, as SQLite's own guide to rebuilding a table says.
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        try
        {
            using var tx = db.Database.BeginTransaction();
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""EmbeddingChunks_new"" (
                    ""Id""              INTEGER NOT NULL CONSTRAINT ""PK_EmbeddingChunks"" PRIMARY KEY AUTOINCREMENT,
                    ""DocumentId""      INTEGER NULL,
                    ""ContextSourceId"" INTEGER NULL,
                    ""Kind""            TEXT NULL,
                    ""OwnerUserId""     INTEGER NULL,
                    ""Buyer""           TEXT NULL,
                    ""StyleNumber""     TEXT NULL,
                    ""SourceRef""       TEXT NULL,
                    ""Text""            TEXT NOT NULL,
                    ""Vector""          BLOB NOT NULL,
                    ""CreatedAt""       TEXT NOT NULL,
                    CONSTRAINT ""FK_EmbeddingChunks_ContextSources_ContextSourceId""
                        FOREIGN KEY (""ContextSourceId"") REFERENCES ""ContextSources"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_EmbeddingChunks_Documents_DocumentId""
                        FOREIGN KEY (""DocumentId"") REFERENCES ""Documents"" (""Id"") ON DELETE CASCADE
                );");
            // Column names from the fixed list above, never input: built as text on purpose.
            var copy = $@"INSERT INTO ""EmbeddingChunks_new"" ({columns}) SELECT {columns} FROM ""EmbeddingChunks"";";
            db.Database.ExecuteSqlRaw(copy);
            db.Database.ExecuteSqlRaw(@"DROP TABLE ""EmbeddingChunks"";");
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""EmbeddingChunks_new"" RENAME TO ""EmbeddingChunks"";");
            foreach (var column in new[] { "DocumentId", "OwnerUserId", "Buyer", "StyleNumber", "ContextSourceId", "Kind" })
            {
                var createIndex = $@"CREATE INDEX ""IX_EmbeddingChunks_{column}"" ON ""EmbeddingChunks"" (""{column}"");";
                db.Database.ExecuteSqlRaw(createIndex);
            }
            tx.Commit();
        }
        finally
        {
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        }
        AddChunkLineColumns(db);
    }

    private static void AddChunkLineColumns(AppDbContext db)
    {
        AddColumnIfMissing(db, "EmbeddingChunks", "LineFrom", @"ALTER TABLE ""EmbeddingChunks"" ADD COLUMN ""LineFrom"" INTEGER NULL;");
        AddColumnIfMissing(db, "EmbeddingChunks", "LineTo", @"ALTER TABLE ""EmbeddingChunks"" ADD COLUMN ""LineTo"" INTEGER NULL;");
    }

    private static bool ColumnIsNotNull(AppDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        OpenIfNeeded(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(3) == 1;
        return false;
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

        // UserTabPermissions, the tab grants from before role presets, is no longer
        // created: EnsureAccessSchema moves its rows into UserPermissionOverrides
        // and leaves the old table as it was.
    }

    /// <summary>
    /// Access (docs/RBAC_ROLES_PROMPT.md §3.2): role presets, user overrides and
    /// the change log, for a database made before them — a new file gets the same
    /// shapes from EnsureCreated. On every database, the triggers that make the
    /// log append-only, the Rulebook's pattern.
    ///
    /// The tab grants move exactly once: in the step that creates
    /// UserPermissionOverrides, each UserTabPermissions row becomes an "allow"
    /// override on "tab:&lt;key&gt;", logged as a change by the system. The old
    /// table is left untouched and never read again.
    /// </summary>
    private static void EnsureAccessSchema(AppDbContext db)
    {
        if (!TableExists(db, "RolePermissions"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""RolePermissions"" (
                    ""Role""            TEXT NOT NULL,
                    ""Key""             TEXT NOT NULL,
                    ""Allowed""         INTEGER NOT NULL,
                    ""UpdatedByUserId"" INTEGER NULL,
                    ""UpdatedAtUtc""    TEXT NOT NULL,
                    CONSTRAINT ""PK_RolePermissions"" PRIMARY KEY (""Role"", ""Key"")
                );");
        }

        if (!TableExists(db, "AccessChangeLog"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""AccessChangeLog"" (
                    ""Id""       INTEGER NOT NULL CONSTRAINT ""PK_AccessChangeLog"" PRIMARY KEY AUTOINCREMENT,
                    ""AtUtc""    TEXT NOT NULL,
                    ""ByUserId"" INTEGER NULL,
                    ""Scope""    TEXT NOT NULL,
                    ""Target""   TEXT NOT NULL,
                    ""Key""      TEXT NOT NULL,
                    ""Before""   TEXT NULL,
                    ""After""    TEXT NULL,
                    ""Note""     TEXT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_AccessChangeLog_AtUtc"" ON ""AccessChangeLog"" (""AtUtc"");");
        }

        if (!TableExists(db, "UserPermissionOverrides"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""UserPermissionOverrides"" (
                    ""UserId""          INTEGER NOT NULL,
                    ""Key""             TEXT NOT NULL,
                    ""Effect""          TEXT NOT NULL,
                    ""UpdatedByUserId"" INTEGER NULL,
                    ""UpdatedAtUtc""    TEXT NOT NULL,
                    ""Note""            TEXT NULL,
                    CONSTRAINT ""PK_UserPermissionOverrides"" PRIMARY KEY (""UserId"", ""Key""),
                    CONSTRAINT ""FK_UserPermissionOverrides_Users_UserId""
                        FOREIGN KEY (""UserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
                );");

            if (TableExists(db, "UserTabPermissions"))
            {
                const string note = "Tab grant moved from UserTabPermissions when role presets arrived.";
                db.Database.ExecuteSqlRaw(@"
                    INSERT OR IGNORE INTO ""UserPermissionOverrides"" (""UserId"", ""Key"", ""Effect"", ""UpdatedByUserId"", ""UpdatedAtUtc"", ""Note"")
                    SELECT g.""UserId"", 'tab:' || g.""TabKey"", 'allow', NULL, g.""CreatedAt"", {0}
                    FROM ""UserTabPermissions"" g JOIN ""Users"" u ON u.""Id"" = g.""UserId"";", note);
                db.Database.ExecuteSqlRaw(@"
                    INSERT INTO ""AccessChangeLog"" (""AtUtc"", ""ByUserId"", ""Scope"", ""Target"", ""Key"", ""Before"", ""After"", ""Note"")
                    SELECT strftime('%Y-%m-%d %H:%M:%S', 'now'), NULL, 'user', CAST(""UserId"" AS TEXT), ""Key"", NULL, 'allow', ""Note""
                    FROM ""UserPermissionOverrides"";");
            }
        }

        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS ""AccessChangeLog_NoUpdate"" BEFORE UPDATE ON ""AccessChangeLog""
            BEGIN SELECT RAISE(ABORT, 'AccessChangeLog is append-only: a logged change is never updated'); END");
        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS ""AccessChangeLog_NoDelete"" BEFORE DELETE ON ""AccessChangeLog""
            BEGIN SELECT RAISE(ABORT, 'AccessChangeLog is append-only: a logged change is never deleted'); END");
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

    /// <summary>
    /// Runs a literal ADD COLUMN only when the column is absent, so the upgrade is
    /// idempotent on a database that already has it.
    /// </summary>
    /// <summary>
    /// মানব সম্পদ · নিয়মাবলি (docs/HR_RULEBOOK_PROMPT.md §3.1). The two tables, for
    /// a database made before the Rulebook (EnsureCreated makes them on a new
    /// file, from the EF model, with the same shape), and on every database the
    /// triggers that make HrRulePageVersions append-only: SQLite itself refuses an
    /// UPDATE or DELETE of a saved version, whoever sends it. Run() reaches here on
    /// a new file too (EnsureCreated has made Conversations by then), so a fresh
    /// database and an upgraded one end up with the same triggers.
    /// </summary>
    public static void EnsureHrRulebookSchema(AppDbContext db)
    {
        if (!TableExists(db, "HrRulePages"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""HrRulePages"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_HrRulePages"" PRIMARY KEY AUTOINCREMENT,
                    ""Title"" TEXT COLLATE NOCASE NOT NULL,
                    ""Category"" TEXT NOT NULL,
                    ""Language"" TEXT NOT NULL,
                    ""Status"" TEXT NOT NULL,
                    ""CurrentVersion"" INTEGER NOT NULL,
                    ""ConfirmedVersion"" INTEGER NULL,
                    ""LibraryItemId"" INTEGER NULL,
                    ""CreatedByUserId"" INTEGER NOT NULL,
                    ""CreatedAtUtc"" TEXT NOT NULL,
                    ""UpdatedAtUtc"" TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(
                @"CREATE UNIQUE INDEX ""IX_HrRulePages_Title"" ON ""HrRulePages"" (""Title"") WHERE ""Status"" <> 'retired';");
            db.Database.ExecuteSqlRaw(
                @"CREATE INDEX ""IX_HrRulePages_LibraryItemId"" ON ""HrRulePages"" (""LibraryItemId"");");
        }

        if (!TableExists(db, "HrRulePageVersions"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""HrRulePageVersions"" (
                    ""PageId"" INTEGER NOT NULL,
                    ""Version"" INTEGER NOT NULL,
                    ""Kind"" TEXT NOT NULL,
                    ""Body"" TEXT NOT NULL,
                    ""ChangeNote"" TEXT NULL,
                    ""AuthorUserId"" INTEGER NOT NULL,
                    ""AtUtc"" TEXT NOT NULL,
                    ""SourcesJson"" TEXT NULL,
                    ""ModelJson"" TEXT NULL,
                    CONSTRAINT ""PK_HrRulePageVersions"" PRIMARY KEY (""PageId"", ""Version""),
                    CONSTRAINT ""FK_HrRulePageVersions_HrRulePages_PageId"" FOREIGN KEY (""PageId"")
                        REFERENCES ""HrRulePages"" (""Id"") ON DELETE RESTRICT
                );");
        }

        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS ""HrRulePageVersions_NoUpdate"" BEFORE UPDATE ON ""HrRulePageVersions""
            BEGIN SELECT RAISE(ABORT, 'HrRulePageVersions is append-only: a saved version is never updated'); END");
        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS ""HrRulePageVersions_NoDelete"" BEFORE DELETE ON ""HrRulePageVersions""
            BEGIN SELECT RAISE(ABORT, 'HrRulePageVersions is append-only: a saved version is never deleted'); END");
    }

    /// <summary>
    /// মানব সম্পদ · প্রসিডিউর (docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.1, §3.3): the
    /// tables for a database made before the Procedure Analyzer, the same shape
    /// EnsureCreated gives a new file, and on every database the triggers that make
    /// HrProcedureVersions and HrLookupLog append-only, the Rulebook's pattern.
    /// </summary>
    public static void EnsureHrProcedureSchema(AppDbContext db)
    {
        if (!TableExists(db, "HrProcedures"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""HrProcedures"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_HrProcedures"" PRIMARY KEY AUTOINCREMENT,
                    ""ObjectName"" TEXT COLLATE NOCASE NOT NULL,
                    ""Kind"" TEXT NOT NULL,
                    ""CurrentVersion"" INTEGER NOT NULL,
                    ""CreatedAtUtc"" TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX ""IX_HrProcedures_ObjectName"" ON ""HrProcedures"" (""ObjectName"");");
        }
        if (!TableExists(db, "HrProcedureVersions"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""HrProcedureVersions"" (
                    ""ProcedureId"" INTEGER NOT NULL,
                    ""Version"" INTEGER NOT NULL,
                    ""Sql"" TEXT NOT NULL,
                    ""Sha256"" TEXT NOT NULL,
                    ""UploadedByUserId"" INTEGER NOT NULL,
                    ""AtUtc"" TEXT NOT NULL,
                    ""Note"" TEXT NULL,
                    ""FactsJson"" TEXT NOT NULL,
                    CONSTRAINT ""PK_HrProcedureVersions"" PRIMARY KEY (""ProcedureId"", ""Version""),
                    CONSTRAINT ""FK_HrProcedureVersions_HrProcedures_ProcedureId"" FOREIGN KEY (""ProcedureId"")
                        REFERENCES ""HrProcedures"" (""Id"") ON DELETE RESTRICT
                );");
        }
        if (!TableExists(db, "HrLookups"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""HrLookups"" (
                    ""Column"" TEXT COLLATE NOCASE NOT NULL,
                    ""Value"" TEXT COLLATE NOCASE NOT NULL,
                    ""Label"" TEXT NULL,
                    ""Note"" TEXT NULL,
                    ""Source"" TEXT NULL,
                    ""UpdatedByUserId"" INTEGER NULL,
                    ""UpdatedAtUtc"" TEXT NOT NULL,
                    CONSTRAINT ""PK_HrLookups"" PRIMARY KEY (""Column"", ""Value"")
                );");
        }
        if (!TableExists(db, "HrLookupLog"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""HrLookupLog"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_HrLookupLog"" PRIMARY KEY AUTOINCREMENT,
                    ""Column"" TEXT NOT NULL,
                    ""Value"" TEXT NOT NULL,
                    ""OldLabel"" TEXT NULL,
                    ""NewLabel"" TEXT NULL,
                    ""OldNote"" TEXT NULL,
                    ""NewNote"" TEXT NULL,
                    ""Reason"" TEXT NOT NULL,
                    ""ChangedByUserId"" INTEGER NOT NULL,
                    ""AtUtc"" TEXT NOT NULL
                );");
        }
        if (!TableExists(db, "HrReviewQuestions"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""HrReviewQuestions"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_HrReviewQuestions"" PRIMARY KEY AUTOINCREMENT,
                    ""Kind"" TEXT NOT NULL,
                    ""Key"" TEXT NOT NULL,
                    ""Column"" TEXT NULL,
                    ""Value"" TEXT NULL,
                    ""Text"" TEXT NOT NULL,
                    ""Status"" TEXT NOT NULL,
                    ""AnswerLabel"" TEXT NULL,
                    ""AnswerNote"" TEXT NULL,
                    ""AnsweredByUserId"" INTEGER NULL,
                    ""AnsweredAtUtc"" TEXT NULL,
                    ""CreatedAtUtc"" TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX ""IX_HrReviewQuestions_Key"" ON ""HrReviewQuestions"" (""Key"");");
        }
        if (!TableExists(db, "HrQuestionOccurrences"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""HrQuestionOccurrences"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_HrQuestionOccurrences"" PRIMARY KEY AUTOINCREMENT,
                    ""QuestionId"" INTEGER NOT NULL,
                    ""ProcedureId"" INTEGER NOT NULL,
                    ""Version"" INTEGER NOT NULL,
                    ""ObjectName"" TEXT NOT NULL,
                    ""Section"" TEXT NULL,
                    ""Line"" INTEGER NOT NULL,
                    ""Snippet"" TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX ""IX_HrQuestionOccurrences_QuestionId_ProcedureId_Version_Line""
                ON ""HrQuestionOccurrences"" (""QuestionId"", ""ProcedureId"", ""Version"", ""Line"");");
        }

        // Literal statements, one per trigger: no SQL is ever built from a value.
        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS ""HrProcedureVersions_NoUpdate"" BEFORE UPDATE ON ""HrProcedureVersions""
            BEGIN SELECT RAISE(ABORT, 'HrProcedureVersions is append-only: a saved version is never updated'); END");
        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS ""HrProcedureVersions_NoDelete"" BEFORE DELETE ON ""HrProcedureVersions""
            BEGIN SELECT RAISE(ABORT, 'HrProcedureVersions is append-only: a saved version is never deleted'); END");
        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS ""HrLookupLog_NoUpdate"" BEFORE UPDATE ON ""HrLookupLog""
            BEGIN SELECT RAISE(ABORT, 'HrLookupLog is append-only: a logged change is never updated'); END");
        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS ""HrLookupLog_NoDelete"" BEFORE DELETE ON ""HrLookupLog""
            BEGIN SELECT RAISE(ABORT, 'HrLookupLog is append-only: a logged change is never deleted'); END");
    }

    /// <summary>
    /// Creates every table of the EF model whose name starts with
    /// <paramref name="prefix"/> and is missing, with its indexes, using the
    /// statements EF's own create script has for it — so a database made before a
    /// module gets exactly the shapes EnsureCreated gives a new file, with no
    /// second copy of the DDL to drift. Used for Matrix's Mx* tables.
    /// </summary>
    private static void EnsureTablesFromModel(AppDbContext db, string prefix)
    {
        var script = db.Database.GenerateCreateScript();
        var statements = script.Split(";", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var create = new System.Text.RegularExpressions.Regex(@"^CREATE TABLE ""(?<t>[^""]+)""");
        var index = new System.Text.RegularExpressions.Regex(@"^CREATE (UNIQUE )?INDEX ""[^""]+"" ON ""(?<t>[^""]+)""");

        var missing = statements
            .Select(s => create.Match(s))
            .Where(m => m.Success && m.Groups["t"].Value.StartsWith(prefix, StringComparison.Ordinal) && !TableExists(db, m.Groups["t"].Value))
            .Select(m => m.Groups["t"].Value)
            .ToHashSet();
        if (missing.Count == 0) return;

        foreach (var s in statements)
        {
            var t = create.Match(s) is { Success: true } c ? c.Groups["t"].Value
                  : index.Match(s) is { Success: true } i ? i.Groups["t"].Value : null;
            if (t is not null && missing.Contains(t)) db.Database.ExecuteSqlRaw(s + ";");
        }
    }

    private static void AddColumnIfMissing(AppDbContext db, string table, string column, string ddl)
    {
        if (!ColumnExists(db, table, column)) db.Database.ExecuteSqlRaw(ddl);
    }

    /// <summary>
    /// Which models each role may run (Rbac/ModelPolicy.cs). Only needed for a
    /// database that predates the feature: on a fresh file EnsureCreated() builds
    /// it from the EF model. The ROWS are seeded in RbacSeeder, which runs for
    /// both cases — Run() above returns early on a brand-new database.
    /// </summary>
    private static void EnsureRoleModelPolicyTable(AppDbContext db)
    {
        if (TableExists(db, "RoleModelPolicies"))
        {
            RelaxRoleModelPolicyUpdatedBy(db);
            return;
        }

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE ""RoleModelPolicies"" (
                ""Role""            TEXT NOT NULL CONSTRAINT ""PK_RoleModelPolicies"" PRIMARY KEY,
                ""AllowedModels""   TEXT NOT NULL DEFAULT '',
                ""DefaultModel""    TEXT NOT NULL DEFAULT '',
                ""UpdatedAtUtc""    TEXT NOT NULL,
                ""UpdatedByUserId"" INTEGER NULL
            );");
    }

    /// <summary>
    /// A RoleModelPolicies table made by the earlier local model-policy code
    /// declares UpdatedByUserId NOT NULL, with 0 for a seeded row. The model now
    /// says NULL for "written by the seeder", so seeding a role added later (the
    /// HrAssistant row) failed on such a database and the API did not start.
    ///
    /// SQLite cannot relax a NOT NULL in place, so the table is copied once, while
    /// the column is still NOT NULL. Every row is kept — an administrator's edits
    /// included — and the old seeder marker 0 becomes NULL.
    /// </summary>
    private static void RelaxRoleModelPolicyUpdatedBy(AppDbContext db)
    {
        if (!ColumnIsNotNull(db, "RoleModelPolicies", "UpdatedByUserId")) return;

        using var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE ""RoleModelPolicies_new"" (
                ""Role""            TEXT NOT NULL CONSTRAINT ""PK_RoleModelPolicies"" PRIMARY KEY,
                ""AllowedModels""   TEXT NOT NULL DEFAULT '',
                ""DefaultModel""    TEXT NOT NULL DEFAULT '',
                ""UpdatedAtUtc""    TEXT NOT NULL,
                ""UpdatedByUserId"" INTEGER NULL
            );");
        db.Database.ExecuteSqlRaw(@"
            INSERT INTO ""RoleModelPolicies_new"" (""Role"", ""AllowedModels"", ""DefaultModel"", ""UpdatedAtUtc"", ""UpdatedByUserId"")
            SELECT ""Role"", ""AllowedModels"", ""DefaultModel"", ""UpdatedAtUtc"", NULLIF(""UpdatedByUserId"", 0)
            FROM ""RoleModelPolicies"";");
        db.Database.ExecuteSqlRaw(@"DROP TABLE ""RoleModelPolicies"";");
        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""RoleModelPolicies_new"" RENAME TO ""RoleModelPolicies"";");
        tx.Commit();
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
                    ""CreatedAt""        TEXT NOT NULL,
                    ""ExtractionModel""  TEXT NOT NULL DEFAULT '',
                    ""Rules""            TEXT NOT NULL DEFAULT '',
                    ""Glossary""         TEXT NOT NULL DEFAULT '',
                    ""ExampleRunIds""    TEXT NOT NULL DEFAULT '',
                    ""Version""          INTEGER NOT NULL DEFAULT 1
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
                    ""CreatedAt""         TEXT NOT NULL,
                    ""Model""             TEXT NULL,
                    ""ProfileVersion""    INTEGER NULL,
                    ""PromptHash""        TEXT NULL,
                    ""RepairUsed""        INTEGER NOT NULL DEFAULT 0,
                    ""FirstResultJson""   TEXT NULL,
                    ""PromptTokens""      INTEGER NULL,
                    ""OutputTokens""      INTEGER NULL,
                    ""DurationMs""        INTEGER NULL
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

        // PO Studio: a run has to remember HOW it was made, or accuracy cannot be
        // compared across a profile edit or a model change. Each statement is a
        // literal — the column list is fixed in code, never user input.
        AddColumnIfMissing(db, "ExtractionRuns", "Model",
            @"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""Model"" TEXT NULL;");
        AddColumnIfMissing(db, "ExtractionRuns", "ProfileVersion",
            @"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""ProfileVersion"" INTEGER NULL;");
        AddColumnIfMissing(db, "ExtractionRuns", "PromptHash",
            @"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""PromptHash"" TEXT NULL;");
        AddColumnIfMissing(db, "ExtractionRuns", "RepairUsed",
            @"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""RepairUsed"" INTEGER NOT NULL DEFAULT 0;");
        AddColumnIfMissing(db, "ExtractionRuns", "FirstResultJson",
            @"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""FirstResultJson"" TEXT NULL;");
        AddColumnIfMissing(db, "ExtractionRuns", "PromptTokens",
            @"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""PromptTokens"" INTEGER NULL;");
        AddColumnIfMissing(db, "ExtractionRuns", "OutputTokens",
            @"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""OutputTokens"" INTEGER NULL;");
        AddColumnIfMissing(db, "ExtractionRuns", "DurationMs",
            @"ALTER TABLE ""ExtractionRuns"" ADD COLUMN ""DurationMs"" INTEGER NULL;");

        // PO Studio: a profile carries its own extraction knowledge — the model
        // to use, buyer rules, a glossary and up to two confirmed runs as worked
        // examples. Version is bumped on every save so a run can record which
        // revision of the profile produced it.
        AddColumnIfMissing(db, "BuyerProfiles", "ExtractionModel",
            @"ALTER TABLE ""BuyerProfiles"" ADD COLUMN ""ExtractionModel"" TEXT NOT NULL DEFAULT '';");
        AddColumnIfMissing(db, "BuyerProfiles", "Rules",
            @"ALTER TABLE ""BuyerProfiles"" ADD COLUMN ""Rules"" TEXT NOT NULL DEFAULT '';");
        AddColumnIfMissing(db, "BuyerProfiles", "Glossary",
            @"ALTER TABLE ""BuyerProfiles"" ADD COLUMN ""Glossary"" TEXT NOT NULL DEFAULT '';");
        AddColumnIfMissing(db, "BuyerProfiles", "ExampleRunIds",
            @"ALTER TABLE ""BuyerProfiles"" ADD COLUMN ""ExampleRunIds"" TEXT NOT NULL DEFAULT '';");
        AddColumnIfMissing(db, "BuyerProfiles", "Version",
            @"ALTER TABLE ""BuyerProfiles"" ADD COLUMN ""Version"" INTEGER NOT NULL DEFAULT 1;");

        // Seed the one template that has a dedicated parser.
        var kohls = db.Database
            .SqlQueryRaw<int>(@"SELECT COUNT(*) AS ""Value"" FROM ""BuyerProfiles"" WHERE ""Name"" = 'Kohls';")
            .AsEnumerable().First();
        if (kohls == 0)
        {
            db.Database.ExecuteSqlRaw(@"
                INSERT INTO ""BuyerProfiles""
                    (""Name"", ""DetectPattern"", ""ParserKey"", ""ChunkingStrategy"", ""IsActive"", ""CreatedAt"",
                     ""ExtractionModel"", ""Rules"", ""Glossary"", ""ExampleRunIds"", ""Version"")
                VALUES
                    ('Kohls', 'SPS Commerce|Kohls|Order #:|Vendors Style', 'KohlsSps', 'none', 1, {0},
                     '', '', '', '', 1);",
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

    /// <summary>
    /// The Oddity gateway's tables, matching what EnsureCreated makes for a new file.
    /// OddityRequests has no foreign key by design: its rows outlive the key.
    /// </summary>
    private static void EnsureOddityTables(AppDbContext db)
    {
        if (!TableExists(db, "OddityKeys"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""OddityKeys"" (
                    ""Id""               INTEGER NOT NULL CONSTRAINT ""PK_OddityKeys"" PRIMARY KEY AUTOINCREMENT,
                    ""OwnerUserId""      INTEGER NOT NULL,
                    ""Name""             TEXT NOT NULL,
                    ""Note""             TEXT NULL,
                    ""Prefix""           TEXT NOT NULL,
                    ""KeyHash""          TEXT NOT NULL,
                    ""DefaultProfileId"" INTEGER NULL,
                    ""RatePerMinute""    INTEGER NULL,
                    ""Enabled""          INTEGER NOT NULL,
                    ""ExpiresAtUtc""     TEXT NULL,
                    ""CreatedAtUtc""     TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_OddityKeys_OwnerUserId"" ON ""OddityKeys"" (""OwnerUserId"");");
            db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX ""IX_OddityKeys_Prefix"" ON ""OddityKeys"" (""Prefix"");");
        }

        if (!TableExists(db, "OdditySettings"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""OdditySettings"" (
                    ""Id""                   INTEGER NOT NULL CONSTRAINT ""PK_OdditySettings"" PRIMARY KEY AUTOINCREMENT,
                    ""Enabled""              INTEGER NOT NULL,
                    ""DefaultModel""         TEXT NOT NULL,
                    ""AllowedModels""        TEXT NOT NULL,
                    ""NumCtx""               INTEGER NOT NULL,
                    ""ReplyReserve""         INTEGER NOT NULL,
                    ""KeepAlive""            TEXT NOT NULL,
                    ""DefaultRatePerMinute"" INTEGER NOT NULL,
                    ""MaxInlineFileMb""      INTEGER NOT NULL,
                    ""LogBodies""            TEXT NOT NULL,
                    ""RetentionDays""        INTEGER NOT NULL,
                    ""UpdatedAtUtc""         TEXT NOT NULL,
                    ""UpdatedByUserId""      INTEGER NULL,
                    ""PublicBaseUrl""        TEXT NOT NULL
                );");
        }
        else if (!ColumnExists(db, "OdditySettings", "PublicBaseUrl"))
        {
            // Phase 2 databases predate the public gateway URL.
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""OdditySettings"" ADD COLUMN ""PublicBaseUrl"" TEXT NOT NULL DEFAULT '';");
        }

        if (!TableExists(db, "OddityRequests"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""OddityRequests"" (
                    ""Id""                    INTEGER NOT NULL CONSTRAINT ""PK_OddityRequests"" PRIMARY KEY AUTOINCREMENT,
                    ""TimestampUtc""          TEXT NOT NULL,
                    ""CorrelationId""         TEXT NULL,
                    ""OwnerUserId""           INTEGER NOT NULL,
                    ""KeyId""                 INTEGER NOT NULL,
                    ""KeyName""               TEXT NOT NULL,
                    ""ClientIp""              TEXT NULL,
                    ""UserAgent""             TEXT NULL,
                    ""Endpoint""              TEXT NOT NULL,
                    ""RequestedModel""        TEXT NULL,
                    ""Model""                 TEXT NULL,
                    ""Profile""               TEXT NULL,
                    ""Stream""                INTEGER NOT NULL,
                    ""StatusCode""            INTEGER NOT NULL,
                    ""Outcome""               TEXT NOT NULL,
                    ""Error""                 TEXT NULL,
                    ""DoneReason""            TEXT NULL,
                    ""PromptTokens""          INTEGER NULL,
                    ""CachedPromptTokens""    INTEGER NULL,
                    ""OutputTokens""          INTEGER NULL,
                    ""LoadMs""                INTEGER NULL,
                    ""PromptMs""              INTEGER NULL,
                    ""GenerationMs""          INTEGER NULL,
                    ""FirstByteMs""           INTEGER NULL,
                    ""TotalMs""               INTEGER NOT NULL,
                    ""EstimatedPromptTokens"" INTEGER NULL,
                    ""AddedTokens""           INTEGER NULL,
                    ""ReceiptJson""           TEXT NULL,
                    ""RequestPreview""        TEXT NULL,
                    ""ResponsePreview""       TEXT NULL,
                    ""RequestBytes""          INTEGER NOT NULL,
                    ""ResponseBytes""         INTEGER NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_OddityRequests_KeyId_TimestampUtc"" ON ""OddityRequests"" (""KeyId"", ""TimestampUtc"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_OddityRequests_OwnerUserId_TimestampUtc"" ON ""OddityRequests"" (""OwnerUserId"", ""TimestampUtc"");");
        }
    }

    private static void EnsureObservabilityTables(AppDbContext db)
    {
        if (!TableExists(db, "ActivityLogs"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""ActivityLogs"" (
                    ""Id""             INTEGER NOT NULL CONSTRAINT ""PK_ActivityLogs"" PRIMARY KEY AUTOINCREMENT,
                    ""TimestampUtc""   TEXT NOT NULL,
                    ""Level""          TEXT NOT NULL,
                    ""Category""       TEXT NOT NULL,
                    ""CorrelationId""  TEXT NULL,
                    ""UserId""         INTEGER NULL,
                    ""UserName""       TEXT NULL,
                    ""Method""         TEXT NULL,
                    ""Path""           TEXT NULL,
                    ""QueryString""    TEXT NULL,
                    ""StatusCode""     INTEGER NULL,
                    ""DurationMs""     INTEGER NULL,
                    ""ClientIp""       TEXT NULL,
                    ""UserAgent""      TEXT NULL,
                    ""Module""         TEXT NULL,
                    ""EventName""      TEXT NULL,
                    ""Message""        TEXT NULL,
                    ""PropertiesJson"" TEXT NULL,
                    ""Environment""    TEXT NOT NULL,
                    ""MachineName""    TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_ActivityLogs_TimestampUtc"" ON ""ActivityLogs"" (""TimestampUtc"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_ActivityLogs_Category_TimestampUtc"" ON ""ActivityLogs"" (""Category"", ""TimestampUtc"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_ActivityLogs_CorrelationId"" ON ""ActivityLogs"" (""CorrelationId"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_ActivityLogs_UserId_TimestampUtc"" ON ""ActivityLogs"" (""UserId"", ""TimestampUtc"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_ActivityLogs_StatusCode_TimestampUtc"" ON ""ActivityLogs"" (""StatusCode"", ""TimestampUtc"");");
        }

        if (!TableExists(db, "AuditLogs"))
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE ""AuditLogs"" (
                    ""Id""             INTEGER NOT NULL CONSTRAINT ""PK_AuditLogs"" PRIMARY KEY AUTOINCREMENT,
                    ""TimestampUtc""   TEXT NOT NULL,
                    ""CorrelationId""  TEXT NULL,
                    ""UserId""         INTEGER NULL,
                    ""EntityName""     TEXT NOT NULL,
                    ""EntityKey""      TEXT NOT NULL,
                    ""Action""         TEXT NOT NULL,
                    ""ChangesJson""    TEXT NULL,
                    ""Environment""    TEXT NOT NULL
                );");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_AuditLogs_TimestampUtc"" ON ""AuditLogs"" (""TimestampUtc"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_AuditLogs_EntityName_EntityKey"" ON ""AuditLogs"" (""EntityName"", ""EntityKey"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_AuditLogs_CorrelationId"" ON ""AuditLogs"" (""CorrelationId"");");
            db.Database.ExecuteSqlRaw(@"CREATE INDEX ""IX_AuditLogs_UserId"" ON ""AuditLogs"" (""UserId"");");
        }
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
