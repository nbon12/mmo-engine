using MmoEngine.Harness.Store;
using Testcontainers.ClickHouse;

[assembly: AssemblyFixture(typeof(MmoEngine.Harness.Tests.Support.ClickHouseFixture))]

namespace MmoEngine.Harness.Tests.Support;

/// <summary>
/// One real ClickHouse server for the test assembly, through Testcontainers (spec 014, Clarifications: store
/// tests use a real ClickHouse, as persistence tests use a real PostgreSQL). Tests share it safely because
/// each uses its own run IDs and sweep names and never asserts global counts (constitution, Isolation and
/// rerun safety). The resource reaper removes the container if a run is killed.
/// </summary>
public sealed class ClickHouseFixture : IAsyncLifetime
{
    /// <summary>The image the tests run against; the local stack pins the same version.</summary>
    public const string Image = "clickhouse/clickhouse-server:25.8";

    private readonly ClickHouseContainer container = new ClickHouseBuilder(Image).Build();

    public string ConnectionString => container.GetConnectionString();

    public ClickHouseStore CreateStore() => new(ConnectionString);

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync().ConfigureAwait(false);
        using var store = CreateStore();
        await store.ApplySchemaAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
