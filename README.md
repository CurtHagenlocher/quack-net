# quack-net

A pure-managed .NET client and Apache Arrow ADBC driver for DuckDB's `quack`
remote protocol.

> Status: experimental. The wire format (quack protocol version 3, as served
> by the `quack` extension of DuckDB `v2.0-cyanoptera`) and DuckDB's
> server-side support are still in flux. Treat this as a moving target.

## What's in the box

| Project | Purpose |
| --- | --- |
| `Quack` | Pure-managed .NET client. Connect, execute SQL, stream `DataChunk` results, append rows. No native dependencies. |
| `Quack.Adbc` | Apache Arrow [ADBC](https://arrow.apache.org/adbc/) driver built on top of `Quack`. Exposes `AdbcDatabase` / `AdbcConnection` / `AdbcStatement` and round-trips Arrow `RecordBatch`es to/from DuckDB. |
| `Quack.Adbc.Native` | A Native AOT shim that builds `Quack.Adbc` into a self-contained C-ABI shared library (`quack_adbc.dll` on Windows, `.so` / `.dylib` elsewhere) loadable from Python, Rust, C, R, or anywhere else that speaks the ADBC C API. |

## Server requirements

You need DuckDB **v2.0.x** with the `quack` core extension: quack protocol
version 3, data serialized for storage version `V2_0_0` (verified against
DuckDB v2.0.1 with quack `3bc63c0`). DuckDB 1.5's quack speaks protocol
version 1 and is not supported by this client any more; the server rejects
the handshake. The integration test fixture installs the extension on demand:

```sql
INSTALL quack FROM core;
LOAD quack;
CALL quack_serve('quack:127.0.0.1:9494', token => 'your-token');
```

The integration tests start the DuckDB CLI named by `QUACK_TEST_DUCKDB`
(default `C:\src\duckdb\duckdb.exe`).

### What protocol version 3 means for clients

- **Heartbeat lease.** The server keeps a session only while it hears from it
  at least once per heartbeat timeout (60 s by default,
  `QuackConnectionOptions.HeartbeatTimeoutSeconds`). `QuackConnection` sends a
  heartbeat about every third of that in the background; an expired lease
  surfaces as `QuackSessionExpiredException`.
- **One statement per connection.** A new query supersedes the one still
  running on the same connection (it fails with *superseded by a new query*;
  with quack `3bc63c0` the newer one can fail too, with *query failed*). Use
  one `QuackConnection` per concurrent query.
- **Cancel.** Cancelling the `CancellationToken` of `ExecuteAsync`,
  `AppendAsync` or `GetChunksAsync` interrupts the query on the server and
  keeps the connection usable (it throws `OperationCanceledException`). Only
  if the server does not stop the query within 10 s is the HTTP request
  aborted, and then the connection is unusable (`QuackConnection.IsUsable`).
  `QuackConnection.CancelAsync(queryUuid)` sends the interrupt directly
  (`QuackQueryResult.QueryUuid`; `Int128.Zero` = whatever runs). The ADBC
  driver's `command_timeout_seconds` uses the same path.
- **Fetch.** Results arrive in dense, numbered batches (the leading ones inline
  with PREPARE); the client checks the announced batch count and acknowledges
  a complete result.
- **Append.** `AppendAsync` runs one `INSERT INTO t SELECT * FROM
  scan_data_from_quack_client(...)` per call and streams the chunks as
  `SEND_DATA` batches (cut at 2048 rows); it returns the inserted row count.
  Rows map to columns by position, as before.
- **Errors** carry the server's exception type
  (`QuackException.ServerExceptionType`, e.g. `Catalog`).

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

## Using the native library from Python (or any ADBC C-API consumer)

Each release publishes a self-contained native library per platform:

| Platform | Asset |
| --- | --- |
| Windows x64   | `quack_adbc-<version>-win-x64.dll` |
| Windows arm64 | `quack_adbc-<version>-win-arm64.dll` |
| Linux x64     | `quack_adbc-<version>-linux-x64.so` |
| Linux arm64   | `quack_adbc-<version>-linux-arm64.so` |
| macOS arm64   | `quack_adbc-<version>-osx-arm64.dylib` |

Download the right one, install `adbc-driver-manager`, and load it:

```python
import adbc_driver_manager.dbapi

with adbc_driver_manager.dbapi.connect(
    driver="path/to/quack_adbc.so",   # or .dll / .dylib
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

The library is fully self-contained: no .NET runtime, no DuckDB native
library, no other dependencies at the call site.

## Using the Power BI connector

Each release also includes a Power BI custom connector (`quack.mez`). To set it
up:

1. **Enable unsigned connectors.** In Power BI Desktop, go to
   **File → Options and settings → Options → Security** and under
   **Data Extensions** select **(Not Recommended) Allow any extension to load
   without validation or warning**. Restart Power BI Desktop.

2. **Copy the native ADBC driver.** Download the appropriate
   `quack_adbc-<version>-win-<arch>.dll` from the release assets and copy it
   into a new folder under the Power BI Desktop installation directory:

   ```
   <Power BI Desktop install>\bin\ADBC Drivers\Quack\quack_adbc.dll
   ```

   > This may require administrator privileges. Rename the file to
   > `quack_adbc.dll` (dropping the version and RID suffix).

3. **Install the custom connector.** Copy `quack.mez` into your Power BI
   custom connectors folder. This is typically one of:

   ```
   %USERPROFILE%\Documents\Power BI Desktop\Custom Connectors\
   %USERPROFILE%\OneDrive - Microsoft\Documents\Power BI Desktop\Custom Connectors\
   ```

   Create the `Custom Connectors` folder if it doesn't already exist.

4. **Restart Power BI Desktop** if it was already running.

When connecting, enter the URI from your `quack_serve` call (e.g.
`quack:127.0.0.1:9494`) as the server address. When prompted for credentials,
choose **Key** and enter the token you specified in the `quack_serve` call.

## Building from source

Prereqs: .NET 10 SDK. The Native AOT build additionally needs the host's
native toolchain — MSVC v143+ on Windows (Visual Studio 2022 or 2026 build
tools), clang on Linux, and the Xcode command-line tools on macOS.

```bash
# Managed build + unit tests (no server required, cross-platform)
dotnet build
dotnet test test/Quack.Tests

# Integration + ADBC tests (require a local duckdb with the quack extension;
# point QuackServerFixture.DuckDbExePath at it)
dotnet test test/Quack.IntegrationTests
dotnet test test/Quack.Adbc.Tests

# Native AOT publish for the host platform (cross-platform; uses pwsh)
pwsh scripts/publish-native.ps1
# Or explicitly cross-RID:
pwsh scripts/publish-native.ps1 -Runtime linux-arm64
# -> src/Quack.Adbc.Native/bin/Release/net10.0/<rid>/publish/<lib>

# End-to-end smoke test (spawns duckdb + loads native lib from Python)
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
powerbi/                   Power BI custom connector (packaged as quack.mez)
scripts/
  publish-native.ps1     wrapper that handles vswhere PATH issues
  smoke_test_python.py   Python load-and-query smoke test
```

## License

[Apache License 2.0](./LICENSE).
