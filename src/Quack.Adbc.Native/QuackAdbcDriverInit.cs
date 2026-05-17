// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.C;
using Quack.Adbc;

namespace Quack.Adbc.Native;

// The C ABI entrypoint for the AOT-compiled native DLL. ADBC's driver-manager
// (and Python's adbc_driver_manager.AdbcDatabase(driver=..., entrypoint=...))
// resolves this exported symbol, then calls it once at load time to populate
// the function-pointer table in *driver*.
//
// Wire shape (ADBC 1.1.0):
//   AdbcStatusCode QuackAdbcDriverInit(
//       int version,
//       CAdbcDriver* driver,
//       CAdbcError* error);
//
// All the heavy lifting (mapping managed AdbcDriver methods onto the C struct
// of function pointers) lives inside Apache.Arrow.Adbc's CAdbcDriverExporter.
// We just hand it a freshly constructed QuackAdbcDriver and forward.
internal static class Entrypoint
{
    // ADBC version constants follow MAJOR*1_000_000 + MINOR*1_000 + PATCH.
    private const int AdbcVersion_1_0_0 = 1_000_000;
    private const int AdbcVersion_1_1_0 = 1_001_000;

    [UnmanagedCallersOnly(EntryPoint = "QuackAdbcDriverInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe AdbcStatusCode QuackAdbcDriverInit(int version, CAdbcDriver* driver, CAdbcError* error)
    {
        try
        {
            // Apache.Arrow.Adbc 0.23.0's CAdbcDriverExporter only populates the
            // ADBC 1.0.0 entry points and rejects anything else. Newer driver
            // managers (e.g. python adbc-driver-manager >= 0.11) advertise
            // 1.1.0 (version 1_001_000) by default. Forward as 1.0.0 — the
            // CAdbcDriver struct shape is the superset and the 1.1.0-only
            // function pointers stay null, which the manager treats as
            // "not supported" rather than as an error.
            if (version != AdbcVersion_1_0_0 && version != AdbcVersion_1_1_0)
            {
                return AdbcStatusCode.NotImplemented;
            }
            return CAdbcDriverExporter.AdbcDriverInit(
                AdbcVersion_1_0_0, driver, error, new QuackAdbcDriver());
        }
        catch
        {
            return AdbcStatusCode.UnknownError;
        }
    }
}
