using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lineup.Core.Storage;

/// <summary>Owns a deferred SQLite snapshot without taking a writer reservation.</summary>
internal sealed class EpgReadTransaction : IAsyncDisposable
{
    private readonly SqliteTransaction _transaction;
    private readonly IDbContextTransaction _enlistment;

    private EpgReadTransaction(SqliteTransaction transaction, IDbContextTransaction enlistment)
    {
        _transaction = transaction;
        _enlistment = enlistment;
    }

    /// <summary>Starts a coherent read; the first SELECT establishes the snapshot.</summary>
    internal static async Task<EpgReadTransaction> BeginAsync(EpgDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(IsolationLevel.Serializable, deferred: true);
        try
        {
            var enlistment = await context.Database.UseTransactionAsync(transaction, cancellationToken)
                ?? throw new InvalidOperationException("The guide read transaction could not be enlisted.");
            return new EpgReadTransaction(transaction, enlistment);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _enlistment.DisposeAsync();
        }
        finally
        {
            await _transaction.DisposeAsync();
        }
    }
}
