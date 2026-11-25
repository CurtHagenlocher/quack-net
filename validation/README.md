# ADBC validation suite

The [ADBC driver validation suite](https://github.com/adbc-drivers/validation)
run against quack-net's native ADBC driver (`quack_adbc`) and a DuckDB 2.0.x
quack server.

## Running it

Needs Python 3.13, a DuckDB 2.0.1 CLI (it installs the `quack` extension) and
a published native driver:

```bash
dotnet publish src/Quack.Adbc.Native -c Release -r linux-x64   # or win-x64, osx-arm64, ...
python -m venv .venv && . .venv/bin/activate
pip install -r validation/requirements.txt
python validation/run.py --duckdb /path/to/duckdb
```

`run.py` starts the DuckDB CLI on a fresh `quack-validation.duckdb` in a
temporary directory, serving quack on two free loopback ports (plain HTTP and
TLS with DuckDB's self-signed certificate), runs pytest against the TLS one
(`--plain` for the HTTP one; the `test_tls_*` tests are then skipped) and stops
the server. `--driver` (or `QUACK_ADBC_DRIVER`) picks the library, otherwise
the one under `src/Quack.Adbc.Native/bin/Release/net*/<rid>/publish/`; the CLI
can also come from `QUACK_TEST_DUCKDB`. Further arguments go to pytest
(`-q`, `-k ...`, `--junitxml=...`).

The GetInfo tests expect DuckDB v2.0.1 (`vendor_version` in
`tests/quack.py`).

## What is expected to fail

- Two `test_get_table_schema` cases (`type/select/enum`, `type/select/union`)
  are marked xfail: the suite takes the word after FROM as the table name,
  which for these VALUES queries is `(VALUES`. They are strict, so the run
  fails once that changes.
- Skips are features the quirks declare absent, mostly parameter binding and
  prepared statements (quack's protocol carries no parameters).

## Where it comes from

`pytest.ini`, `queries/` and `tests/` are the validation setup of the Rust
quack ADBC driver, adbc-drivers/quack pull request #64 (`rust/validation`,
commit `866fdff`), Apache License 2.0 (the headers are kept). The queries are
unchanged: they expect what DuckDB itself returns, which is what quack-net's
driver returns. Changed for quack-net:

- `tests/quack.py`: driver and vendor names, and the features this driver has
  (GetTableSchema, GetStatistics, ExecuteSchema, ingest into a catalog /
  schema / temporary table, rows affected; no prepare), the secondary schema,
  and temporary tables in `temp.main`.
- `tests/conftest.py`: the driver library path; the xfail and TLS skip above;
  and on Windows a wrapper around the framework's `QuerySet.load` that names
  queries with forward slashes (the framework names them by their relative
  path, which there has backslashes, and collection then fails).
- `tests/test_statement.py`: "an unsupported result keeps the connection
  usable" uses a VARIANT holding an INTERVAL, since quack-net returns BIGNUM;
  the error is raised while the result is read, so Python sees it from the C
  stream as an `OSError`.
- `run.py`, `requirements.txt` (the framework pinned to a commit).
