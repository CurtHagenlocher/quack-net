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

### VARIANT

VARIANT columns arrive as `VariantColumn` with one `DuckDbVariant` per row
(null = SQL NULL; `DuckDbVariantType.Null` = a variant null). Every DuckDB
variant type is decoded losslessly, from both forms DuckDB 2.0 sends: the
canonical layout and SHREDDED vectors (typed columns plus an untyped
overlay; DuckDB sends them for a column cast to VARIANT and for VARIANT
columns a checkpoint shredded). Object fields keep the variant's order; for a
shredded value that is the typed fields first, then the leftover ones. The
values are decoded on first access to `VariantColumn.Values`; until then the
column holds the received vector (and with it the response buffer).

Through ADBC a VARIANT column is `arrow.parquet.variant` (Apache.Arrow's
`VariantArray`) holding the bytes DuckDB's own Arrow export writes (checked
against `variant_to_parquet_variant`), converted straight from the received
vector without building `DuckDbVariant` values. The type mapping is DuckDB's:
unsigned and 128-bit integers widen to the next signed type (UINT32 and
wider to INT64 when the value fits), and INTERVAL, BIGNUM, BITSTRING,
TIMESTAMP_S / _MS, TIME_TZ, TIME_NS, TIMESTAMP_NS WITH TIME ZONE and GEOMETRY
values are refused, as DuckDB refuses them.

The client writes and reads the Parquet Variant bytes itself: Apache.Arrow
23.0.0's `VariantBuilder` and `VariantReader` swap the field-id and
field-offset sizes of an object header (fixed after 23.0.0,
apache/arrow-dotnet#421). With 23.0.0, `VariantArray.GetVariantValue` fails
on an object whose field values take more than 255 bytes, for these values
and for DuckDB's or Spark's; values Apache.Arrow 23.0.0 encoded such objects
in are misread by everyone else, this client included.

VARIANT values can also be sent: `AppendAsync` with a `VariantColumn`
(type `LogicalType.Variant`) and ADBC ingest of `arrow.parquet.variant`. The
client writes DuckDB's canonical layout (values in pre-order, object fields
sorted by key, as DuckDB's own builder writes them); a root variant null is
sent as SQL NULL. Ingested Parquet variant values map like DuckDB's Parquet
reader maps them (TIMESTAMP with time zone to TIMESTAMPTZ, DECIMAL4/8 to the
width of the value's digits, DECIMAL16 to width 38). A shredded
`arrow.parquet.variant` input (with `typed_value`) is merged by Apache.Arrow,
so its leftover object values are read with 23.0.0's header layout.

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

### Bulk ingest

ADBC's standard bulk ingest: set `adbc.ingest.target_table` (plus, as
needed, `adbc.ingest.mode`, `adbc.ingest.target_catalog`,
`adbc.ingest.target_db_schema`, `adbc.ingest.temporary`), bind a batch with
`Bind` or a stream with `BindStream`, and call `ExecuteUpdate`, which returns
the number of rows written.

```csharp
using AdbcStatement ingest = conn.CreateStatement();
ingest.SetOption("adbc.ingest.target_table", "events");
ingest.SetOption("adbc.ingest.mode", "adbc.ingest.mode.create_append");
ingest.BindStream(reader);                 // any IArrowArrayStream
long rows = ingest.ExecuteUpdate().AffectedRows;
```

| mode | statement |
|---|---|
| `create` (the default) | `CREATE TABLE t AS SELECT * FROM <stream>`; fails if t exists |
| `append` | `INSERT INTO t SELECT * FROM <stream>`; fails if t does not exist |
| `replace` | `CREATE OR REPLACE TABLE t AS SELECT * FROM <stream>` |
| `create_append` | `CREATE TABLE IF NOT EXISTS t (...)`, then the append |

The rows travel as one client data stream (`scan_data_from_quack_client`):
the batches are sent as they are read, in batches of about 32 MiB, and each
one is released once it has been serialized. `create`, `replace` and
`append` are a single statement each; `create_append`'s two are not atomic.
A created table takes the Arrow schema's column names and DuckDB types; an
append matches columns by position. A temporary target is `temp.main` and
can't be combined with a catalog or schema.

The older form still works: without `adbc.ingest.target_table`, a bound batch
or stream is appended to the existing table that `SqlQuery` names.

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

Bulk ingest works through the DB-API extension, with every mode:

```python
with conn.cursor() as cur:
    cur.adbc_ingest("events", arrow_table_or_reader, mode="create_append")
```

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

## Planned: a compact column layer (not done)

Measured on DuckDB 2.0.1, 1M rows, client side only (decoding captured
responses), per row:

| column | on the wire | wire -> columns | columns -> Arrow |
|---|---|---|---|
| VARCHAR, short (`'x' \|\| i`) | 11 B | 24-35 ns, 24 B allocated | 24-55 ns, 11 B |
| VARCHAR, 32 B (`md5`) | 36 B | 26-35 ns, 24 B | 37-51 ns, 36 B |
| LIST(BIGINT) `[i, i + 1]` | 25 B | 24-33 ns, 16 B | 3-6 ns, 8 B |
| BIGINT + 2 VARCHAR + LIST | 72 B | 88-127 ns, 72 B | 67-83 ns, 56 B |

The mixed query takes about 500 ns per row end to end through ADBC, so
decoding is about a third of it. Most of that is strings:

- `VarBytesColumn` holds a `ReadOnlyMemory<byte>?` per value: 24 bytes to
  point at a 1-7 byte key or a short string. `ListColumn` holds a
  `(ulong, ulong)` (16 bytes) per row.
- `ArrowConverter` copies every string into a new buffer, value by value,
  although DuckDB sends them contiguous (field 109) and Arrow's string layout
  is the same bytes plus offsets.

What to do:

1. Read VARCHAR/BLOB vectors into int32 offsets (a prefix sum over DuckDB's
   length data) plus the contiguous byte data, and LIST entries as 32-bit
   offset and length.
2. Build Arrow string, binary and list arrays from those offsets and a slice
   of the response buffer, without copying the bytes.
3. Keep `VarBytesColumn.Values` and `ListColumn.Entries` as they are, built on
   first access from the compact form (as `VariantColumn.Values` is), so the
   public API does not change.
4. Measure end to end again; the estimate is about half of the decode time.

Ownership rules this relies on:

- The response is a plain `byte[]` owned by the GC. Columns and Arrow buffers
  slice it; a batch keeps its response alive, and the C Data Interface export
  pins it until the consumer releases the batch. The ADBC path already does
  this for fixed-size columns and validity, so strings add no new kind of
  sharing.
- No pooling of response buffers while slices are handed out: a buffer
  returned to an `ArrayPool` with slices still in use is a use after free.
  Pooling would need an explicit, reference-counted owner that every column
  and Arrow buffer retains and releases.
- A small batch kept by a consumer keeps its whole response (up to 32 MiB)
  alive. If that matters, copy columns that are a small part of the response.
- Slices sit at arbitrary byte offsets. Offsets are built in new, aligned
  arrays; check fixed-size columns, which are sliced today, against a strict
  consumer (`pyarrow` `validate(full=True)`).

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
