using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Potel.Api.Data;

// Eén schrijver tegelijk (BEGIN IMMEDIATE in SQLite). Nodig waar we eerst lezen en daarop schrijven, zoals het volgende
// factuurnummer of uren die nog vrij zijn: een tweede verzoek wacht tot het eerste klaar is en ziet dan de nieuwe stand.
// Zonder CommitAsync wordt alles teruggedraaid. Binnen een transactie die al loopt doet hij niets extra's.
public sealed class WriteLock : IAsyncDisposable
{
    readonly IDbContextTransaction? tx;
    WriteLock(IDbContextTransaction? tx) => this.tx = tx;

    public static async Task<WriteLock> BeginAsync(AppDb db) =>
        new(db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable) : null);

    public Task CommitAsync() => tx?.CommitAsync() ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => tx?.DisposeAsync() ?? ValueTask.CompletedTask;
}
