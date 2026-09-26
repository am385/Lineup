using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Lineup.Core.Storage;

/// <summary>
/// Applies restart-safe schema upgrades to databases created before authoritative lineup storage.
/// </summary>
internal static class EpgDatabaseSchema
{
    private static readonly SemaphoreSlim UpgradeGate = new(1, 1);
    private static readonly HashSet<string> InitializedDataSources = new(StringComparer.Ordinal);

    /// <summary>
    /// Ensures the current model and additive legacy upgrades are available.
    /// </summary>
    /// <param name="context">Database context to upgrade.</param>
    /// <param name="cancellationToken">Cancels the upgrade.</param>
    internal static async Task EnsureAsync(EpgDbContext context, CancellationToken cancellationToken = default)
    {
        await UpgradeGate.WaitAsync(cancellationToken);
        try
        {
            var dataSource = context.Database.GetDbConnection().DataSource;
            var cacheInitialization = !string.IsNullOrWhiteSpace(dataSource) && dataSource != ":memory:";
            if (cacheInitialization && InitializedDataSources.Contains(dataSource))
            {
                return;
            }

            await EnsureCoreAsync(context, cancellationToken);
            if (cacheInitialization)
            {
                InitializedDataSources.Add(dataSource);
            }
        }
        finally
        {
            UpgradeGate.Release();
        }
    }

    private static async Task EnsureCoreAsync(EpgDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS "Channels" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Channels" PRIMARY KEY AUTOINCREMENT,
                    "GuideNumber" TEXT NOT NULL,
                    "GuideName" TEXT NULL,
                    "Affiliate" TEXT NULL,
                    "ImageURL" TEXT NULL,
                    "DRM" INTEGER NOT NULL DEFAULT 0,
                    "Favorite" INTEGER NOT NULL DEFAULT 0,
                    "LastUpdatedUtc" TEXT NOT NULL,
                    "LastSeenImportId" TEXT NULL,
                    "SupplementalXml" TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Channels_GuideNumber" ON "Channels" ("GuideNumber");
                CREATE TABLE IF NOT EXISTS "Programs" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Programs" PRIMARY KEY AUTOINCREMENT,
                    "GuideNumber" TEXT NOT NULL,
                    "Title" TEXT NULL,
                    "EpisodeTitle" TEXT NULL,
                    "Synopsis" TEXT NULL,
                    "StartTime" INTEGER NOT NULL,
                    "EndTime" INTEGER NOT NULL,
                    "ImageURL" TEXT NULL,
                    "PosterURL" TEXT NULL,
                    "EpisodeNumber" TEXT NULL,
                    "OriginalAirdate" INTEGER NULL,
                    "First" INTEGER NULL,
                    "SeriesID" TEXT NULL,
                    "Filter" TEXT NULL,
                    "FetchedAtUtc" TEXT NOT NULL,
                    "LastSeenImportId" TEXT NULL,
                    "SupplementalXml" TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_Programs_GuideNumber" ON "Programs" ("GuideNumber");
                CREATE INDEX IF NOT EXISTS "IX_Programs_StartTime" ON "Programs" ("StartTime");
                CREATE INDEX IF NOT EXISTS "IX_Programs_EndTime" ON "Programs" ("EndTime");
                CREATE TABLE IF NOT EXISTS "LineupChannels" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_LineupChannels" PRIMARY KEY AUTOINCREMENT,
                    "GuideNumber" TEXT NOT NULL,
                    "GuideName" TEXT NOT NULL,
                    "URL" TEXT NOT NULL,
                    "VideoCodec" TEXT NULL,
                    "AudioCodec" TEXT NULL,
                    "HD" INTEGER NOT NULL,
                    "DRM" INTEGER NOT NULL,
                    "Favorite" INTEGER NOT NULL,
                    "Tags" TEXT NULL,
                    "SignalStrength" INTEGER NULL,
                    "SignalQuality" INTEGER NULL,
                    "AdditionalPropertiesJson" TEXT NULL,
                    "IsEnabled" INTEGER NOT NULL DEFAULT 1,
                    "IsActive" INTEGER NOT NULL DEFAULT 1,
                    "FirstSeenUtc" TEXT NOT NULL,
                    "LastSeenUtc" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_LineupChannels_GuideNumber" ON "LineupChannels" ("GuideNumber");
                CREATE INDEX IF NOT EXISTS "IX_LineupChannels_IsActive_IsEnabled" ON "LineupChannels" ("IsActive", "IsEnabled");
                CREATE TABLE IF NOT EXISTS "GuideImports" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_GuideImports" PRIMARY KEY,
                    "StartedUtc" TEXT NOT NULL,
                    "CompletedUtc" TEXT NULL,
                    "CoverageStart" INTEGER NOT NULL,
                    "CoverageEnd" INTEGER NOT NULL,
                    "ChannelCount" INTEGER NOT NULL,
                    "ProgramCount" INTEGER NOT NULL,
                    "SupplementalXml" TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_GuideImports_CompletedUtc" ON "GuideImports" ("CompletedUtc");
                """, cancellationToken);
            await AddColumnIfMissingAsync(connection, "Channels", "DRM", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await AddColumnIfMissingAsync(connection, "Channels", "Favorite", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await AddColumnIfMissingAsync(connection, "Channels", "LastSeenImportId", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "Channels", "SupplementalXml", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "Programs", "LastSeenImportId", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "Programs", "SupplementalXml", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "GuideImports", "SupplementalXml", "TEXT NULL", cancellationToken);
            await ExecuteAsync(connection, """
                DELETE FROM "Programs"
                WHERE "Id" NOT IN (
                    SELECT MIN("Id")
                    FROM "Programs"
                    GROUP BY "GuideNumber", "StartTime", "EndTime", "Title"
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Programs_GuideNumber_StartTime_EndTime_Title"
                ON "Programs" ("GuideNumber", "StartTime", "EndTime", "Title");
                """, cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task AddColumnIfMissingAsync(System.Data.Common.DbConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = $"PRAGMA table_info('{table}')";
        await using var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
        var found = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }

        await reader.DisposeAsync();
        if (!found)
        {
            await ExecuteAsync(connection, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}", cancellationToken);
        }
    }

    private static async Task ExecuteAsync(System.Data.Common.DbConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
