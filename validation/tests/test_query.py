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

from urllib.parse import parse_qsl, urlsplit

import adbc_drivers_validation.tests.query as query_tests
import duckdb
import pytest
from adbc_drivers_validation import compare, model

from .quack import get_quirks


def pytest_generate_tests(metafunc) -> None:
    quirks = [get_quirks(metafunc.config.getoption("vendor_version"))]
    if metafunc.function.__name__ == "test_duckdb_query":
        driver = quirks[0]
        driver_id = f"{driver.name}:{driver.short_version}"
        cases = [
            pytest.param(
                driver_id, case, id=f"{driver_id}:{case.name}", marks=case.pytest_marks
            )
            for case in driver.query_set.queries.values()
            if isinstance(case.query, model.SelectQuery)
            and case.query.bind_query(driver) is None
        ]
        metafunc.parametrize("driver,query", cases, scope="module", indirect=["driver"])
        return
    return query_tests.generate_tests(quirks, metafunc)


@pytest.fixture(scope="module")
def duckdb_client(db_kwargs):
    uri = urlsplit(db_kwargs["uri"])
    options = dict(parse_qsl(uri.query))
    tls = options.get("tls", "true").replace("-", "_")
    with duckdb.connect() as client:
        client.execute("INSTALL quack")
        client.execute("SET TimeZone = 'UTC'")
        if tls == "skip_verify":
            client.execute("SET enable_curl_server_cert_verification = false")
            client.execute("SET enable_server_cert_verification = false")
        endpoint = duckdb.ConstantExpression(f"quack:{uri.netloc}")
        token = duckdb.ConstantExpression(options.get("token", ""))
        disable_ssl = tls == "false"
        client.execute(
            f"ATTACH {endpoint} AS remote (disable_ssl {disable_ssl}, token {token})"
        )
        yield client


class TestQuery(query_tests.TestQuery):
    def test_duckdb_query(self, driver, query, query_setup, duckdb_client):
        result = duckdb_client.sql(
            "SELECT * FROM remote.query(?)", params=[query.query.query()]
        ).to_arrow_table()
        compare.compare_tables(query.query.expected_result(), result, query.metadata())
