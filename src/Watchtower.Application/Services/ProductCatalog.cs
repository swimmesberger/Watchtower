using Microsoft.EntityFrameworkCore;
using Npgsql;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Find-or-create over the product catalogue — the mechanism behind ADR-0026's implicit-product
/// contract: creating a stack (or a template) stays one form with the repository fields on it, and the
/// product is resolved silently behind the request. There is never a "create the product first" step.
/// </summary>
/// <remarks>
/// The lookup matches on <see cref="ProductSourceKey"/>, the same normalization the backfill migration
/// applied, so a stack created after an upgrade lands on the product that upgrade created rather than
/// forking a near-duplicate. Matching happens in memory over the whole catalogue rather than in SQL: a
/// product list is tens of rows, and keeping one implementation of the rule is worth more than an index
/// scan here — a second copy in SQL is exactly the drift the migration comment warns about.
/// </remarks>
public sealed class ProductCatalog(WatchtowerDbContext db) {
    /// <summary>Audit category every product write is recorded under.</summary>
    public const string AuditCategory = "products";

    /// <summary>
    /// Savepoint the speculative insert is rolled back to when it loses a name race. A savepoint rather
    /// than a bare catch because the caller is already inside a transaction, and in PostgreSQL a failed
    /// statement poisons the whole transaction — the retry's own query would fail too.
    /// </summary>
    private const string InsertSavepoint = "wt_product_find_or_create";

    /// <summary>
    /// How many times a create may lose a race before giving up. Three because each retry re-reads the
    /// catalogue, so the only way to keep losing is sustained contention on one name — which is a real
    /// failure worth surfacing rather than looping on.
    /// </summary>
    private const int MaxCreateAttempts = 3;

    /// <summary>
    /// Returns the product for this source, creating it when nothing matches. The second element says
    /// whether it was created, so the caller can audit <c>product.create</c> with its own reason.
    /// </summary>
    /// <remarks>
    /// The new product is added to the change tracker and saved, because the caller needs its id to
    /// point a stack at it. Callers run inside their own transaction, so an implicitly created product
    /// rolls back with the stack that asked for it.
    /// <para>
    /// Two requests can reach the insert with the same source, or with different sources whose derived
    /// names collide; both surface as a unique violation on <c>ix_products_name</c>, and both are
    /// answered the same way — undo the speculative insert, look again, and either adopt the winner or
    /// pick a fresh suffix. Without this the loser of a routine double-submit gets a 500.
    /// </para>
    /// </remarks>
    public async Task<(Product Product, bool Created)> FindOrCreateAsync(
        string repositoryUrl, string composeFilePath, string branch, int? credentialId, CancellationToken ct) {
        for (var attempt = 1; ; attempt++) {
            if (await FindAsync(repositoryUrl, composeFilePath, ct) is { } existing) return (existing, false);

            var product = new Product {
                Name = await UniqueNameAsync(ProductSourceKey.DeriveName(repositoryUrl), ct),
                RepositoryUrl = repositoryUrl.Trim(),
                ComposeFilePath = composeFilePath.Trim(),
                DefaultBranch = branch.Trim(),
                CredentialId = credentialId,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var transaction = db.Database.CurrentTransaction;
            if (transaction is not null) await transaction.CreateSavepointAsync(InsertSavepoint, ct);
            try {
                db.Products.Add(product);
                await db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.ReleaseSavepointAsync(InsertSavepoint, ct);
                return (product, true);
            } catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < MaxCreateAttempts) {
                if (transaction is not null) await transaction.RollbackToSavepointAsync(InsertSavepoint, ct);
                db.Entry(product).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// The product a create request means: the one <paramref name="productId"/> names, or the one the
    /// inline repository fields find-or-create. Shared by <c>stacks.create</c> and
    /// <c>templates.create</c> so both spell the "either/or, never both" rule the same way.
    /// </summary>
    /// <remarks>
    /// A credential supplied alongside repository fields that match an <em>existing</em> product is
    /// refused rather than dropped on the floor: the caller asked for a clone credential and would
    /// otherwise get a stack quietly cloning with a different one. The message is the same shape
    /// <c>stacks.update</c> gives for the other product-owned fields.
    /// </remarks>
    public async Task<(Product? Product, bool Created, AppError? Error)> ResolveAsync(
        int? productId, string? repositoryUrl, string? composeFilePath, string? branch, int? credentialId,
        CancellationToken ct) {
        var hasInlineSource =
            !string.IsNullOrWhiteSpace(repositoryUrl) || !string.IsNullOrWhiteSpace(composeFilePath);

        if (productId is { } id) {
            if (hasInlineSource) {
                return (null, false, AppError.Validation(
                    "Pass either productId or the repository fields, not both — a product owns the "
                    + "repository URL and compose file path (ADR-0026)."));
            }
            var named = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (named is null) return (null, false, AppError.NotFound($"Product {id} not found."));
            return credentialId is { } wanted && wanted != named.CredentialId
                ? (null, false, AppError.Validation(SourceRefusal("git credential", named)))
                : (named, false, null);
        }

        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return (null, false, AppError.Validation("A repository URL (or an existing productId) is required."));
        if (string.IsNullOrWhiteSpace(composeFilePath))
            return (null, false, AppError.Validation("A compose file path is required."));
        if (string.IsNullOrWhiteSpace(branch))
            return (null, false, AppError.Validation("A branch is required."));

        // Checked against the match before creating anything, so the refusal names the product the
        // caller would silently have joined rather than one this call just made.
        if (await FindAsync(repositoryUrl, composeFilePath, ct) is { } match
            && credentialId is { } requested && requested != match.CredentialId) {
            return (null, false, AppError.Validation(SourceRefusal("git credential", match)));
        }

        var (product, created) = await FindOrCreateAsync(repositoryUrl, composeFilePath, branch, credentialId, ct);
        return (product, created, null);
    }

    /// <summary>The product whose normalized source matches, or null.</summary>
    public async Task<Product?> FindAsync(string repositoryUrl, string composeFilePath, CancellationToken ct) {
        var wanted = ProductSourceKey.Create(repositoryUrl, composeFilePath);
        var products = await db.Products.OrderBy(p => p.Id).ToListAsync(ct);
        return products.FirstOrDefault(p => ProductSourceKey.Create(p.RepositoryUrl, p.ComposeFilePath) == wanted);
    }

    /// <summary>
    /// Whether another product already owns this normalized source. The catalogue deliberately allows
    /// several products over one repository (different compose files), but not two over the <em>same</em>
    /// key: <see cref="FindAsync"/> would then have to pick one, and every later <c>stacks.create</c>
    /// would silently route to whichever is older.
    /// </summary>
    /// <param name="excludeProductId">The product being edited, so a save that changes nothing passes.</param>
    public async Task<Product?> FindConflictAsync(
        string repositoryUrl, string composeFilePath, int? excludeProductId, CancellationToken ct) {
        var match = await FindAsync(repositoryUrl, composeFilePath, ct);
        return match is not null && match.Id != excludeProductId ? match : null;
    }

    /// <summary>
    /// <paramref name="preferred"/>, or the first free <c>name-2</c>, <c>name-3</c>, … A name derived
    /// from a repository path collides as soon as two hosts serve a repository of the same name, and an
    /// implicit creation must never fail on a name the caller never chose. The unique index is still the
    /// enforcement — see <see cref="FindOrCreateAsync"/> for the race this loses cleanly.
    /// </summary>
    public async Task<string> UniqueNameAsync(string preferred, CancellationToken ct) {
        var taken = await db.Products
            .Where(p => p.Name == preferred || p.Name.StartsWith(preferred + "-"))
            .Select(p => p.Name)
            .ToListAsync(ct);
        if (!taken.Contains(preferred, StringComparer.Ordinal)) return preferred;
        for (var suffix = 2; ; suffix++) {
            var candidate = $"{preferred}-{suffix}";
            if (!taken.Contains(candidate, StringComparer.Ordinal)) return candidate;
        }
    }

    /// <summary>
    /// The refusal every surface gives for a product-owned field somebody tried to set from a stack or a
    /// template. One wording, so the answer to "why can't I change this here?" reads the same wherever
    /// it is met — and always names the call that <em>can</em> change it.
    /// </summary>
    public static string SourceRefusal(string field, Product product) =>
        $"The {field} belongs to product '{product.Name}' since ADR-0026 and cannot be set from a stack "
        + $"or a template. Use products.update (product {product.Id}) — the change then applies to every "
        + "stack and template deploying it.";

    /// <summary>A write that lost a race on a unique index, as opposed to any other write failure.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
