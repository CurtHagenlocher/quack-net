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
import adbc_drivers_validation.tests.ingest as ingest_tests
import pyarrow
import pytest
from adbc_drivers_validation import model
from adbc_drivers_validation.utils import execute_query_without_prepare

from .quack import get_quirks


def pytest_generate_tests(metafunc) -> None:
    quirks = [get_quirks(metafunc.config.getoption("vendor_version"))]
    return ingest_tests.generate_tests(quirks, metafunc)


class TestIngest(ingest_tests.TestIngest):
    def test_append_schema_mismatch(self, driver, conn, query) -> None:
        subquery = query.query
        assert isinstance(subquery, model.IngestQuery)

        table_name = ingest_tests.make_table_name("test_ingest_append_mismatch", query)
        data = subquery.input()
        mismatched = data.append_column(
            "adbc_extra_column", pyarrow.array([0] * len(data), type=pyarrow.int64())
        )

        with conn.cursor() as cursor:
            driver.try_drop_table(cursor, table_name=table_name)
            with driver.setup_statement(query, cursor):
                cursor.adbc_ingest(table_name, data, mode="create")
                with pytest.raises(adbc_driver_manager.dbapi.Error) as excinfo:
                    cursor.adbc_ingest(table_name, mismatched, mode="append")

        assert (
            excinfo.value.status_code
            == adbc_driver_manager.AdbcStatusCode.ALREADY_EXISTS
        )

    @pytest.mark.requires_features(["statement_bulk_ingest", "connection_transactions"])
    def test_bulk_ingest_rolls_back_with_manual_transaction(
        self, driver, driver_path: str, db_kwargs: dict
    ) -> None:
        table_name = "test_bulk_ingest_manual_rollback"
        data = pyarrow.table({"idx": [1], "value": ["a"]})

        with (
            adbc_driver_manager.dbapi.connect(
                driver=driver_path, db_kwargs=db_kwargs, autocommit=True
            ) as cleanup_conn,
            cleanup_conn.cursor() as cursor,
        ):
            driver.try_drop_table(cursor, table_name=table_name)

        with adbc_driver_manager.dbapi.connect(
            driver=driver_path, db_kwargs=db_kwargs, autocommit=False
        ) as conn:
            with conn.cursor() as cursor:
                cursor.adbc_ingest(table_name, data, mode="create")

            conn.rollback()

            with (
                conn.cursor() as cursor,
                pytest.raises(adbc_driver_manager.dbapi.Error) as excinfo,
            ):
                execute_query_without_prepare(
                    cursor,
                    f"SELECT * FROM {driver.quote_identifier(table_name)}",
                )

        assert driver.is_table_not_found(table_name, excinfo.value)
