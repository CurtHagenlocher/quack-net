# Copyright (c) Curt Hagenlocher. All rights reserved.
# Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

"""Runs the ADBC validation suite against a DuckDB quack server.

Starts a DuckDB CLI (2.0.x, with the quack extension) on a fresh database in a
temporary directory, serving quack on two free loopback ports, plain HTTP and
TLS (DuckDB's self-signed certificate), then runs pytest with QUACK_URI
pointing at one of them and stops the server. Extra arguments go to pytest.

    python run.py --duckdb <duckdb CLI> [--driver <quack_adbc library>] [--plain] [pytest args]
"""

import argparse
import os
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOKEN = "quack-validation-token"

INIT_SQL = """\
INSTALL quack;
LOAD quack;
SET GLOBAL TimeZone = 'UTC';
SELECT * FROM quack_serve('quack:127.0.0.1:{plain}', token => '{token}', disable_ssl => true);
SELECT * FROM quack_serve('quack:127.0.0.1:{tls}', token => '{token}', disable_ssl => false);
CREATE SCHEMA quack_validation_secondary;
"""


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_for(port: int, server: subprocess.Popen, log: Path, timeout: float = 120) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if server.poll() is not None:
            sys.exit(f"The DuckDB server exited ({server.returncode}) before serving port {port}.\n"
                     + log.read_text(encoding="utf-8", errors="replace"))
        try:
            socket.create_connection(("127.0.0.1", port), timeout=1).close()
            return
        except OSError:
            time.sleep(0.5)
    sys.exit(f"The DuckDB server did not serve port {port} within {timeout} s.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--duckdb", default=os.environ.get("QUACK_TEST_DUCKDB"),
                        help="DuckDB 2.0.x CLI (default: $QUACK_TEST_DUCKDB)")
    parser.add_argument("--driver", default=os.environ.get("QUACK_ADBC_DRIVER"),
                        help="quack_adbc library (default: $QUACK_ADBC_DRIVER, else the published one)")
    parser.add_argument("--plain", action="store_true", help="connect over plain HTTP instead of TLS")
    args, pytest_args = parser.parse_known_args()
    if not args.duckdb:
        parser.error("pass --duckdb or set QUACK_TEST_DUCKDB")

    plain, tls = free_port(), free_port()
    with tempfile.TemporaryDirectory(prefix="quack-validation-") as work:
        Path(work, "init.sql").write_text(INIT_SQL.format(plain=plain, tls=tls, token=TOKEN), encoding="utf-8")
        # The catalog the suite expects is the database's name: quack-validation.
        # The CLI serves until its stdin closes.
        log = Path(work, "server.log")
        with open(log, "w", encoding="utf-8") as log_file:
            server = subprocess.Popen(
                [args.duckdb, "quack-validation.duckdb", "-init", "init.sql"],
                cwd=work, stdin=subprocess.PIPE, stdout=log_file, stderr=subprocess.STDOUT, text=True)
        try:
            wait_for(plain, server, log)
            wait_for(tls, server, log)
            env = dict(os.environ)
            env["QUACK_URI"] = (f"quack://127.0.0.1:{plain}/?token={TOKEN}&tls=false" if args.plain
                                else f"quack://127.0.0.1:{tls}/?token={TOKEN}&tls=skip_verify")
            if args.driver:
                env["QUACK_ADBC_DRIVER"] = str(Path(args.driver).resolve())
            tests = sorted(str(p.relative_to(HERE)) for p in (HERE / "tests").glob("test_*.py"))
            return subprocess.run([sys.executable, "-m", "pytest", *tests, "-p", "no:cacheprovider", *pytest_args],
                                  cwd=HERE, env=env).returncode
        finally:
            server.stdin.close()
            try:
                server.wait(timeout=30)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()


if __name__ == "__main__":
    sys.exit(main())
