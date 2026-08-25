using Watchtower.Application.Tests;
using Xunit;

// One PostgreSQL for the whole assembly (ADR-0024) — see the twin file in Watchtower.Application.Tests.
// The fixture type is the linked PostgresTestServer.cs; each test assembly is its own process, so each
// gets its own server unless WATCHTOWER_TEST_PG points both at one.
[assembly: AssemblyFixture(typeof(PostgresTestServerFixture))]

// On Windows every materialized test chain strands an intermediate in the user's certificate store;
// unswept they eventually break chain building machine-wide. See the fixture for the mechanism.
[assembly: AssemblyFixture(typeof(LeakedCertificateCleanupFixture))]
