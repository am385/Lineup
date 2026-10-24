using Microsoft.EntityFrameworkCore;

namespace Lineup.Core.Storage;

/// <summary>
/// Creates the current database schema after the startup schema gate has rejected unsupported versions.
/// </summary>
internal static class EpgDatabaseSchema
{
    private static readonly SemaphoreSlim UpgradeGate = new(1, 1);
    private static readonly HashSet<string> InitializedDataSources = new(StringComparer.Ordinal);

    /// <summary>
    /// Ensures the current model and schema metadata are available.
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
        var dataSource = context.Database.GetDbConnection().DataSource;
        if (!string.IsNullOrWhiteSpace(dataSource))
        {
            var schemaState = StartupSchemaGate.CheckDatabase(dataSource);
            if (schemaState.IsFactoryResetRequired)
            {
                throw new FactoryResetRequiredException(schemaState.Reason!);
            }
        }

        await context.Database.EnsureCreatedAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await ExecuteAsync(connection, StartupSchemaGate.CreateDatabaseMetadataSql(), cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }


    private static async Task ExecuteAsync(System.Data.Common.DbConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
