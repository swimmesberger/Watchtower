using Elarion.Abstractions;
using Elarion.Abstractions.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Watchtower.Application.Pipeline;

/// <summary>
/// Turns a lost optimistic-concurrency race into a <see cref="ErrorKind.Conflict"/> result instead of
/// an unhandled exception (ADR-0024 decision 3).
/// </summary>
/// <remarks>
/// <para>
/// Editable rows carry PostgreSQL's <c>xmin</c> as their EF concurrency token, so a handler that reads
/// a row, decides something about it and writes it back fails its <c>SaveChangesAsync</c> when another
/// writer got there first. That is the correct outcome, and it is a domain one — "someone else changed
/// this, look again" — not a fault. Without this the caller would see a 500 and a stack trace for a
/// situation the UI can state in a sentence.
/// </para>
/// <para>
/// One decorator on the whole assembly rather than a try/catch per handler: which handlers can lose the
/// race is a property of the model (which entities carry a token), not of any handler, so a per-handler
/// rule would be a list to keep in step with the configurations. The
/// <c>IResultFailureFactory</c> constraint scopes it at compile time to the handlers that return a
/// <c>Result</c>, which the generator honours by simply not attaching it elsewhere.
/// </para>
/// </remarks>
public sealed class ConcurrencyConflictDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner
) : IHandler<TRequest, TResponse>
    where TResponse : IResultFailureFactory<TResponse> {
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        try {
            return await inner.HandleAsync(request, ct);
        } catch (DbUpdateConcurrencyException) {
            return TResponse.Failure(AppError.Conflict(
                "Someone else changed this while you were editing it. Reload and try again."));
        }
    }
}
