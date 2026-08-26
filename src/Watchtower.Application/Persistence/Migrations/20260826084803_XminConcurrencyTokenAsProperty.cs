using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <summary>
    /// <b>Intentionally empty — do not delete it as dead weight.</b> The six entities that carry
    /// PostgreSQL's <c>xmin</c> as their concurrency token moved it from an EF <em>shadow</em> property
    /// to a real <c>uint Xmin</c> property on the entity (see <c>IHasXmin</c> and <c>XminConcurrency</c>;
    /// the reasoning is the provider maintainer's in npgsql/efcore.pg#3539).
    /// </summary>
    /// <remarks>
    /// <c>xmin</c> is a PostgreSQL <em>system</em> column: it is not in any <c>CREATE TABLE</c>, so
    /// renaming the property that maps to it changes nothing a migration can express — which is why both
    /// halves below are empty and why <c>has-pending-model-changes</c> was already clean without this
    /// file. What this migration exists for is the <em>model snapshot</em>, which records property names:
    /// without it the snapshot would keep describing a shadow property that no longer exists, and the
    /// next real migration would be diffed against a model that is subtly not this one.
    /// </remarks>
    public partial class XminConcurrencyTokenAsProperty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No operations: see the class remarks. The change is invisible below the model.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo.
        }
    }
}
