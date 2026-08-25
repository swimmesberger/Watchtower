using Watchtower.Application.Tests;
using Xunit;

// One PostgreSQL for the whole assembly (ADR-0024): the fixture exists only so the container this run
// may have started is stopped when the last test finishes, rather than left to Testcontainers' reaper.
[assembly: AssemblyFixture(typeof(PostgresTestServerFixture))]

// On Windows every materialized test chain strands an intermediate in the user's certificate store;
// unswept they eventually break chain building machine-wide. See the fixture for the mechanism.
[assembly: AssemblyFixture(typeof(LeakedCertificateCleanupFixture))]
