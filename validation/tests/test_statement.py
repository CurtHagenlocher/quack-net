# Copyright (c) 2026 ADBC Drivers Contributors
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#         http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

import adbc_driver_manager.dbapi
import adbc_drivers_validation.tests.statement as statement_tests
import pyarrow as pa
import pytest

from .quack import get_quirks


def pytest_generate_tests(metafunc) -> None:
    quirks = [get_quirks(metafunc.config.getoption("vendor_version"))]
    return statement_tests.generate_tests(quirks, metafunc)


class TestStatement(statement_tests.TestStatement):
    # quack-net returns every DuckDB type (BIGNUM as DuckDB's Arrow export
    # does). A VARIANT holding an INTERVAL has no Arrow form (the Parquet
    # variant spec has no interval); that is found while the batch is
    # converted, so it reaches Python through the C stream, as an OSError
    # (get_next returns only an errno).
    def test_unsupported_result_preserves_connection(self, conn):
        with conn.cursor() as cursor:
            with pytest.raises(OSError, match="Parquet VARIANT"):
                cursor.execute("SELECT (INTERVAL '1 day')::VARIANT")
                cursor.fetchall()
            cursor.execute("SELECT 1")
            assert cursor.fetchall() == [(1,)]

    @pytest.mark.parametrize(
        ("count", "index_type"),
        [
            (255, pa.uint8()),
            (256, pa.uint16()),
            (65535, pa.uint16()),
            (65536, pa.uint32()),
        ],
    )
    def test_enum_width(self, conn, count, index_type):
        name = f"quack_enum_{count}"
        with conn.cursor() as cursor:
            cursor.execute(
                f"CREATE TYPE {name} AS ENUM (SELECT i::VARCHAR FROM range({count}) t(i) ORDER BY i)"
            )
            try:
                cursor.execute(f"SELECT '{count - 1}'::{name} AS res")
                result = cursor.fetch_arrow_table().column(0).chunk(0)
                assert result.type == pa.dictionary(index_type, pa.string())
                assert result.indices.to_pylist() == [count - 1]
                assert result.dictionary[count - 1].as_py() == str(count - 1)
            finally:
                cursor.execute(f"DROP TYPE {name}")
