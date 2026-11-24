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

import dataclasses
import inspect
import os
import sys
from pathlib import Path

import adbc_drivers_validation.model
import adbc_drivers_validation.tests.conftest
import pytest
from adbc_drivers_validation.tests.conftest import (  # noqa: F401
    conn,
    conn_factory,
    db_kwargs,
    manual_test,
    noci,
)
from adbc_drivers_validation.tests.conftest import (
    pytest_collection_modifyitems as _framework_collection_modifyitems,
)

from .quack import get_quirks

REPO = Path(__file__).resolve().parent.parent.parent


# quack-net: on Windows the framework names a query by its path relative to
# the queries folder with backslashes (type\select\int32), and collection then
# fails (test parameters come out NOTSET). Name them with forward slashes, as
# on Linux (the framework's own fix would be .as_posix() in QuerySet.load).
if os.sep != "/" and "as_posix" not in inspect.getsource(adbc_drivers_validation.model.QuerySet.load):
    _load = adbc_drivers_validation.model.QuerySet.load.__func__

    def _load_posix(cls, root, parent=None):
        native = None
        if parent is not None:
            native = dataclasses.replace(parent, queries={k.replace("/", os.sep): v for k, v in parent.queries.items()})
        loaded = _load(cls, root, native)
        loaded.queries = {
            k.replace(os.sep, "/"): dataclasses.replace(q, name=q.name.replace(os.sep, "/"))
            for k, q in loaded.queries.items()
        }
        return loaded

    adbc_drivers_validation.model.QuerySet.load = classmethod(_load_posix)


# quack-net: get_table_schema takes the word after FROM as the table name,
# which for these two queries is "(VALUES".
_FRAMEWORK_LIMITS = {
    "test_get_table_schema[quack:2.0:type/select/enum]",
    "test_get_table_schema[quack:2.0:type/select/union]",
}


def pytest_collection_modifyitems(session, config, items):
    _framework_collection_modifyitems(session, config, items)
    for item in items:
        if item.name in _FRAMEWORK_LIMITS:
            item.add_marker(pytest.mark.xfail(reason="the suite reads the table name '(VALUES' from the query", strict=True))
        # The TLS tests need QUACK_URI to be a TLS endpoint (run.py without --plain).
        if item.name.startswith("test_tls_") and "tls=false" in os.environ.get("QUACK_URI", ""):
            item.add_marker(pytest.mark.skip(reason="QUACK_URI is a plain HTTP endpoint"))


def pytest_addoption(parser):
    adbc_drivers_validation.tests.conftest.pytest_addoption(parser)
    parser.addoption("--vendor-version", action="store", default="2.0")


@pytest.fixture(scope="session")
def driver(request, pytestconfig) -> adbc_drivers_validation.model.DriverQuirks:
    driver = request.param
    assert driver.startswith("quack")
    return get_quirks(pytestconfig.getoption("vendor_version"))


# quack-net: the NativeAOT library from QUACK_ADBC_DRIVER, else the one
# `dotnet publish src/Quack.Adbc.Native -c Release -r <rid>` produced.
@pytest.fixture(scope="session")
def driver_path(driver: adbc_drivers_validation.model.DriverQuirks) -> str:
    if path := os.environ.get("QUACK_ADBC_DRIVER"):
        return path
    ext = {
        "win32": "dll",
        "darwin": "dylib",
    }.get(sys.platform, "so")
    found = sorted(REPO.glob(f"src/Quack.Adbc.Native/bin/Release/net*/*/publish/quack_adbc.{ext}"))
    if not found:
        pytest.fail("No published quack_adbc library; publish src/Quack.Adbc.Native or set QUACK_ADBC_DRIVER.")
    return str(found[-1])
