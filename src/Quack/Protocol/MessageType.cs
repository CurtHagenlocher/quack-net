// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack.Protocol;

// Mirror of duckdb_quack::MessageType
// (src/include/quack_message.hpp in duckdb/duckdb-quack v2.0-cyanoptera, quack version 3).
public enum MessageType : byte
{
    Invalid = 0,
    ConnectionRequest = 1,
    ConnectionResponse = 2,
    PrepareRequest = 3,
    PrepareResponse = 4,
    // 5 and 6 are reserved.
    FetchRequest = 7,
    FetchResponse = 8,
    // Was APPEND_REQUEST in quack version 1 (DuckDB 1.5).
    SendDataRequest = 9,
    SuccessResponse = 10,
    DisconnectMessage = 11,
    CancelRequest = 12,
    // 13 is unused.
    SendDataResponse = 14,
    Acknowledgement = 15,
    HeartbeatRequest = 16,
    ErrorResponse = 100,
}
