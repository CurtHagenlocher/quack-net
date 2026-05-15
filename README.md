# quack-net

A pure-managed .NET client and Apache Arrow ADBC driver for DuckDB's `quack`
remote protocol.

> Status: experimental. The wire format (`v1.5-variegata`) and DuckDB's
> server-side support are still in flux. Treat this as a moving target.

## What's in the box

| Project | Purpose |
| --- | --- |
| `Quack` | Pure-managed .NET client. Connect, execute SQL, stream `DataChunk` results, append rows. No native dependencies. |
| `Quack.Adbc` | Apache Arrow [ADBC](https://arrow.apache.org/adbc/) driver built on top of `Quack`. Exposes `AdbcDatabase` / `AdbcConnection` / `AdbcStatement` and round-trips Arrow `RecordBatch`es to/from DuckDB. |
| `Quack.Adbc.Native` | A Native AOT shim that builds `Quack.Adbc` into a self-contained C-ABI shared library (`quack_adbc.dll` on Windows, `.so` / `.dylib` elsewhere) loadable from Python, Rust, C, R, or anywhere else that speaks the ADBC C API. |

## Server requirements

You need a DuckDB build that supports the `quack` extension. Today that means
DuckDB **v1.5+** with the `quack` extension installed from `core_nightly`, and
the protocol revision pinned to `v1.5-variegata`
(`SerializationCompatibility(7)`). The integration test fixture installs the
extension on demand:

```sql
INSTALL quack FROM core_nightly;
LOAD quack;
CALL quack_serve('quack:127.0.0.1:9494', token => 'your-token');
```

## Using `Quack` from .NET

```csharp
using Quack;

await using QuackConnection conn = await QuackConnection.OpenAsync(
    "quack:127.0.0.1:9494",
    token: "your-token");

QuackQueryResult result = await conn.ExecuteAsync(
    "SELECT id, name FROM users WHERE id < ?",
    parameters: [10]);

await foreach (DuckDbChunk chunk in result.ReadChunksAsync())
{
    // chunk.RowCount, chunk.Columns[0] as IntegerColumn, etc.
}
```

## Using `Quack.Adbc` from .NET

```csharp
using Apache.Arrow.Adbc;
using Quack.Adbc;

QuackAdbcDriver driver = new();
using AdbcDatabase db = driver.Open(new Dictionary<string, string>
{
    [QuackAdbcDriver.UriParameter]   = "quack:127.0.0.1:9494",
    [QuackAdbcDriver.TokenParameter] = "your-token",
});
using AdbcConnection conn = db.Connect(options: null);

using AdbcStatement stmt = conn.CreateStatement();
stmt.SqlQuery = "SELECT 1 AS x";
QueryResult qr = stmt.ExecuteQuery();
RecordBatch? batch = await qr.Stream!.ReadNextRecordBatchAsync();
```

## Using the native DLL from Python

Download `quack_adbc.dll` from a release, install `adbc-driver-manager`, and:

```python
import adbc_driver_manager.dbapi

with adbc_driver_manager.dbapi.connect(
    driver="path/to/quack_adbc.dll",
    entrypoint="QuackAdbcDriverInit",
    db_kwargs={
        "uri":   "quack:127.0.0.1:9494",
        "token": "your-token",
    },
) as conn:
    with conn.cursor() as cur:
        cur.execute("SELECT 1 AS x")
        print(cur.fetch_arrow_table().to_pydict())
```

The DLL is fully self-contained: no .NET runtime, no DuckDB native library,
no other dependencies at the call site.

## Building from source

Prereqs: .NET 10 SDK. For the Native AOT build, also Windows + the Visual
Studio 2026 build tools (MSVC v143+ and the Windows 11 SDK).

```powershell
# Managed build + unit tests (no server required)
dotnet build
dotnet test test/Quack.Tests

# Integration + ADBC tests (require a local duckdb.exe with quack)
#   set DuckDbExePath in QuackServerFixture or accept the default
#   C:\src\duckdb\duckdb.exe
dotnet test test/Quack.IntegrationTests
dotnet test test/Quack.Adbc.Tests

# Native AOT publish (Windows)
pwsh scripts/publish-native.ps1
# -> src/Quack.Adbc.Native/bin/Release/net10.0/win-x64/publish/quack_adbc.dll

# End-to-end smoke test (spawns duckdb + loads DLL from Python)
python scripts/smoke_test_python.py
```

## Repository layout

```
src/
  Quack/                 managed wire protocol client
  Quack.Adbc/            ADBC driver built on Quack
  Quack.Adbc.Native/     Native AOT shim exporting QuackAdbcDriverInit
test/
  Quack.Tests/           unit tests (no server)
  Quack.IntegrationTests/   end-to-end against a real duckdb.exe
  Quack.Adbc.Tests/      ADBC driver tests against the same server
  Quack.TestFixtures/    shared QuackServerFixture
scripts/
  publish-native.ps1     wrapper that handles vswhere PATH issues
  smoke_test_python.py   Python load-and-query smoke test
```

## License

[Apache License 2.0](./LICENSE).
