using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lineup.Core.Storage;

/// <summary>
/// Atomically creates the current database schema and metadata after the startup gate has rejected unsupported versions.
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

        var creator = context.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken))
        {
            await creator.CreateAsync(cancellationToken);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await creator.HasTablesAsync(cancellationToken))
        {
            await creator.CreateTablesAsync(cancellationToken);
        }
        await context.Database.ExecuteSqlRawAsync(StartupSchemaGate.CreateDatabaseMetadataSql(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
