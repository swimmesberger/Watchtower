using Watchtower.Application.Tests;
using Xunit;

// One PostgreSQL for the whole assembly (ADR-0024): the fixture exists only so the container this run
// may have started is stopped when the last test finishes, rather than left to Testcontainers' reaper.
[assembly: AssemblyFixture(typeof(PostgresTestServerFixture))]
