"""Smoke test: load the AOT-built native quack-adbc driver from Python.

Spawns a local DuckDB CLI with the quack extension and starts a quack_serve
listener on a random loopback port, then loads `quack_adbc.dll` via
adbc_driver_manager and exercises a few statements end-to-end.

Run from the repo root:

    python scripts/smoke_test_python.py

Requires:
  - duckdb.exe with the quack extension available at C:\\src\\duckdb\\duckdb.exe
    (override with --duckdb-exe)
  - The native DLL already built via:
        dotnet publish src/Quack.Adbc.Native -c Release -r win-x64
"""

from __future__ import annotations

import argparse
import socket
import subprocess
import sys
import time
import uuid
from pathlib import Path

import adbc_driver_manager.dbapi
import pyarrow as pa


REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_DUCKDB = Path(r"C:\src\duckdb\duckdb.exe")
DEFAULT_DLL = REPO_ROOT / "src" / "Quack.Adbc.Native" / "bin" / "Release" / "net10.0" / "win-x64" / "publish" / "quack_adbc.dll"


def pick_free_port() -> int:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def wait_for_port(port: int, timeout: float = 30.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=1.0):
                return
        except OSError:
            time.sleep(0.15)
    raise TimeoutError(f"DuckDB quack server did not become reachable on port {port}")


def start_duckdb_quack(duckdb_exe: Path, port: int, token: str) -> subprocess.Popen[str]:
    proc = subprocess.Popen(
        [str(duckdb_exe), "-interactive", ":memory:"],
        stdin=subprocess.PIPE,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.STDOUT,
        text=True,
    )
    assert proc.stdin is not None
    proc.stdin.write(".bail on\n")
    proc.stdin.write("INSTALL quack FROM core_nightly;\n")
    proc.stdin.write("LOAD quack;\n")
    proc.stdin.write(f"CALL quack_serve('quack:127.0.0.1:{port}', token => '{token}');\n")
    proc.stdin.flush()
    return proc


def stop_duckdb(proc: subprocess.Popen[str], port: int) -> None:
    if proc.stdin is not None and not proc.stdin.closed:
        try:
            proc.stdin.write(f"CALL quack_stop('quack:127.0.0.1:{port}');\n")
            proc.stdin.write(".exit\n")
            proc.stdin.flush()
            proc.stdin.close()
        except (BrokenPipeError, OSError):
            pass
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()


def run_smoke_test(dll: Path, duckdb_exe: Path) -> None:
    if not dll.exists():
        sys.exit(f"Native DLL not found at {dll}. Did you publish? See script docstring.")
    if not duckdb_exe.exists():
        sys.exit(f"DuckDB not found at {duckdb_exe}. Pass --duckdb-exe.")

    port = pick_free_port()
    token = "test-token-" + uuid.uuid4().hex
    print(f"[smoke] starting DuckDB quack server on 127.0.0.1:{port}", flush=True)
    proc = start_duckdb_quack(duckdb_exe, port, token)
    try:
        wait_for_port(port)
        print(f"[smoke] server ready; loading native DLL: {dll}", flush=True)

        with adbc_driver_manager.dbapi.connect(
            driver=str(dll),
            entrypoint="QuackAdbcDriverInit",
            db_kwargs={"uri": f"quack:127.0.0.1:{port}", "token": token},
        ) as conn:
            print("[smoke] connected via ADBC", flush=True)

            # 1) Trivial SELECT.
            with conn.cursor() as cur:
                cur.execute("SELECT 1 AS x, 'hello' AS y")
                table = cur.fetch_arrow_table()
                assert table.to_pydict() == {"x": [1], "y": ["hello"]}, f"unexpected: {table.to_pydict()}"
                print(f"[smoke] SELECT 1 -> {table.to_pydict()}", flush=True)

            # 2) GetInfo round-trip. Python's driver-manager returns a dict
            # keyed by either the human name or the raw uint32 code depending
            # on the version, so accept either.
            info = conn.adbc_get_info()
            print(f"[smoke] GetInfo keys -> {[str(k) for k in info.keys()]}", flush=True)
            vendor_name = info.get("vendor_name") or info.get(0)
            driver_name = info.get("driver_name") or info.get(100)
            assert vendor_name == "DuckDB", f"vendor_name={vendor_name!r}"
            assert driver_name == "quack-net ADBC", f"driver_name={driver_name!r}"
            print(f"[smoke] GetInfo -> vendor={vendor_name}, driver={driver_name}", flush=True)

            # 3) CREATE + INSERT + SELECT.
            with conn.cursor() as cur:
                cur.execute("CREATE TABLE smoke (id INTEGER, name VARCHAR)")
                cur.execute("INSERT INTO smoke VALUES (1, 'alpha'), (2, 'beta')")
                cur.execute("SELECT id, name FROM smoke ORDER BY id")
                rows = cur.fetchall()
                assert rows == [(1, "alpha"), (2, "beta")], f"unexpected: {rows}"
                print(f"[smoke] SELECT rows -> {rows}", flush=True)

            # 4) GetTableSchema for the table we just made.
            schema = conn.adbc_get_table_schema("smoke")
            assert [f.name for f in schema] == ["id", "name"], f"unexpected schema: {schema}"
            print(f"[smoke] GetTableSchema -> {[f.name + ':' + str(f.type) for f in schema]}", flush=True)

            # 5) Bulk ingest (adbc.ingest.* options + a bound stream).
            def batch(start: int, count: int) -> pa.RecordBatch:
                return pa.record_batch(
                    [pa.array(range(start, start + count), pa.int64()),
                     pa.array([f"n{i}" for i in range(start, start + count)], pa.string())],
                    names=["id", "name"],
                )

            def rows(sql: str):
                with conn.cursor() as cur:
                    cur.execute(sql)
                    return cur.fetchall()

            reader = pa.RecordBatchReader.from_batches(batch(0, 0).schema, [batch(0, 3), batch(3, 0), batch(3, 2)])
            with conn.cursor() as cur:
                assert cur.adbc_ingest("ingested", reader, mode="create") == 5
                assert cur.adbc_ingest("ingested", batch(5, 2), mode="append") == 2
                assert cur.adbc_ingest("ingested2", pa.Table.from_batches([batch(0, 4)]), mode="create_append") == 4
                assert cur.adbc_ingest("ingested2", batch(4, 1), mode="create_append") == 1
                assert cur.adbc_ingest("ingested2", batch(100, 1), mode="replace") == 1
                assert cur.adbc_ingest("tmp_ingest", batch(0, 2), mode="create", temporary=True) == 2
            assert rows("SELECT count(*), min(id), max(id) FROM ingested") == [(7, 0, 6)]
            assert rows("SELECT column_name, data_type FROM duckdb_columns() WHERE table_name = 'ingested' ORDER BY column_index") == [
                ("id", "BIGINT"), ("name", "VARCHAR")]
            assert rows("SELECT id, name FROM ingested2") == [(100, "n100")]
            assert rows("SELECT count(*) FROM temp.main.tmp_ingest") == [(2,)]
            print("[smoke] adbc_ingest create/append/create_append/replace/temporary -> ok", flush=True)

        print("[smoke] ALL CHECKS PASSED", flush=True)
    finally:
        stop_duckdb(proc, port)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, default=DEFAULT_DLL)
    parser.add_argument("--duckdb-exe", type=Path, default=DEFAULT_DUCKDB)
    args = parser.parse_args()
    run_smoke_test(args.dll, args.duckdb_exe)


if __name__ == "__main__":
    main()
