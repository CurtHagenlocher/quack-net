// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Protocol;
using Quack.Serialization;
using Quack.Tests.Data;
using Quack.Types;

namespace Quack.Tests.Protocol;

// Quack version 3 (duckdb-quack v2.0-cyanoptera): the messages, the chunk blob
// after the body, and the DuckDB 2.0 string vector layout.
public class QuackV3MessageTests
{
    private static readonly LogicalType IntType = new(LogicalTypeId.Integer);
    private static readonly LogicalType VarcharType = new(LogicalTypeId.Varchar);

    [Fact]
    public void ConnectionRequest_CarriesClientIdAndHeartbeat()
    {
        ConnectionRequestMessage original = new()
        {
            AuthString = "tok",
            MinSupportedQuackVersion = 3,
            MaxSupportedQuackVersion = 3,
            ClientId = "client-1",
            HeartbeatTimeoutSeconds = 60,
        };
        ConnectionRequestMessage round = Assert.IsType<ConnectionRequestMessage>(QuackMessage.Deserialize(original.ToBytes()));
        Assert.Equal("client-1", round.ClientId);
        Assert.Equal(60UL, round.HeartbeatTimeoutSeconds);
        Assert.Equal(3UL, round.MaxSupportedQuackVersion);
    }

    [Fact]
    public void ConnectionResponse_CarriesHeartbeat()
    {
        ConnectionResponseMessage original = new() { ConnectionId = "c", QuackVersion = 3, HeartbeatTimeoutSeconds = 45 };
        ConnectionResponseMessage round = Assert.IsType<ConnectionResponseMessage>(QuackMessage.Deserialize(original.ToBytes()));
        Assert.Equal(3UL, round.QuackVersion);
        Assert.Equal(45UL, round.HeartbeatTimeoutSeconds);
    }

    [Fact]
    public void PrepareRequest_QueryUuidAndInlineRows_RoundTrip()
    {
        Int128 uuid = ((Int128)42 << 64) | 7;
        PrepareRequestMessage zero = new() { ConnectionId = "c", SqlQuery = "SELECT 1", QueryUuid = uuid, InlineRows = 0 };
        PrepareRequestMessage round = Assert.IsType<PrepareRequestMessage>(QuackMessage.Deserialize(zero.ToBytes()));
        Assert.Equal(uuid, round.QueryUuid);
        Assert.Equal(0UL, round.InlineRows);

        PrepareRequestMessage unset = zero with { InlineRows = null };
        byte[] unsetBytes = unset.ToBytes();
        Assert.Null(Assert.IsType<PrepareRequestMessage>(QuackMessage.Deserialize(unsetBytes)).InlineRows);
        // An unset inline_rows is omitted, not written as (idx_t)-1.
        Assert.True(unsetBytes.Length < zero.ToBytes().Length);
    }

    [Fact]
    public void FetchResponse_ChunksTravelAsBlobAfterBody()
    {
        DuckDbChunk chunk = ChunkBuilder.OneColumn(IntType,
            new FixedSizeColumn { Type = IntType, Count = 3, ElementSize = 4, Data = ChunkBuilder.Int32Bytes(1, 2, 3) });
        FetchResponseMessage original = new() { Results = [chunk, chunk], BatchIndex = 5 };

        FetchResponseMessage round = Assert.IsType<FetchResponseMessage>(QuackMessage.Deserialize(original.ToBytes()));
        Assert.Equal(5UL, round.BatchIndex);
        Assert.Null(round.TotalBatches);
        Assert.Equal(2, round.Results.Count);
        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(round.Results[1].Columns[0]);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(2)));
    }

    [Fact]
    public void FetchResponse_Terminal_HasNoChunksAndTotal()
    {
        FetchResponseMessage original = new() { TotalBatches = 4 };
        FetchResponseMessage round = Assert.IsType<FetchResponseMessage>(QuackMessage.Deserialize(original.ToBytes()));
        Assert.True(round.IsEndOfStream);
        Assert.Equal(4UL, round.TotalBatches);
    }

    [Fact]
    public void SendData_BatchAndTerminal_RoundTrip()
    {
        DuckDbChunk chunk = ChunkBuilder.OneColumn(VarcharType, new VarBytesColumn
        {
            Type = VarcharType,
            Count = 2,
            Values = [(ReadOnlyMemory<byte>)"ab"u8.ToArray(), (ReadOnlyMemory<byte>)"c"u8.ToArray()],
        });
        SendDataRequestMessage batch = new() { ConnectionId = "c", StreamId = "s-1", BatchIndex = 1, Chunks = [chunk] };
        SendDataRequestMessage roundBatch = Assert.IsType<SendDataRequestMessage>(QuackMessage.Deserialize(batch.ToBytes()));
        Assert.Equal("s-1", roundBatch.StreamId);
        Assert.Equal(1UL, roundBatch.BatchIndex);
        Assert.Null(roundBatch.TotalBatches);
        VarBytesColumn strs = Assert.IsType<VarBytesColumn>(Assert.Single(roundBatch.Chunks).Columns[0]);
        Assert.Equal("ab"u8.ToArray(), strs.Values[0]!.Value.ToArray());

        SendDataRequestMessage terminal = new() { ConnectionId = "c", StreamId = "s-1", TotalBatches = 1 };
        SendDataRequestMessage roundTerminal = Assert.IsType<SendDataRequestMessage>(QuackMessage.Deserialize(terminal.ToBytes()));
        Assert.Empty(roundTerminal.Chunks);
        Assert.Null(roundTerminal.BatchIndex);
        Assert.Equal(1UL, roundTerminal.TotalBatches);
    }

    [Fact]
    public void SendDataResponse_AcceptBudgetIsAlwaysWritten()
    {
        SendDataResponseMessage unbounded = new();
        Assert.Null(Assert.IsType<SendDataResponseMessage>(QuackMessage.Deserialize(unbounded.ToBytes())).AcceptBudget);
        SendDataResponseMessage bounded = new() { AcceptBudget = 9 };
        Assert.Equal(9UL, Assert.IsType<SendDataResponseMessage>(QuackMessage.Deserialize(bounded.ToBytes())).AcceptBudget);
    }

    [Fact]
    public void Cancel_Acknowledgement_Heartbeat_RoundTrip()
    {
        Int128 uuid = 123456789;
        Assert.Equal(uuid, Assert.IsType<CancelRequestMessage>(
            QuackMessage.Deserialize(new CancelRequestMessage { ConnectionId = "c", QueryUuid = uuid }.ToBytes())).QueryUuid);
        Assert.Equal(Int128.Zero, Assert.IsType<CancelRequestMessage>(
            QuackMessage.Deserialize(new CancelRequestMessage { ConnectionId = "c" }.ToBytes())).QueryUuid);
        Assert.Equal(uuid, Assert.IsType<AcknowledgementMessage>(
            QuackMessage.Deserialize(new AcknowledgementMessage { ConnectionId = "c", QueryUuid = uuid }.ToBytes())).QueryUuid);
        HeartbeatRequestMessage heartbeat = Assert.IsType<HeartbeatRequestMessage>(
            QuackMessage.Deserialize(new HeartbeatRequestMessage { ConnectionId = "c" }.ToBytes()));
        Assert.Equal("c", heartbeat.ConnectionId);
    }

    [Fact]
    public void ErrorResponse_TypeExtraInfoAndInvalidate_RoundTrip()
    {
        ErrorResponse original = new()
        {
            Message = "Table with name x does not exist!",
            ExceptionType = "Catalog",
            ExtraInfo = new Dictionary<string, string> { ["name"] = "x", ["type"] = "Table" },
            MustInvalidate = true,
        };
        ErrorResponse round = Assert.IsType<ErrorResponse>(QuackMessage.Deserialize(original.ToBytes()));
        Assert.Equal("Catalog", round.ExceptionType);
        Assert.Equal("x", round.ExtraInfo["name"]);
        Assert.Equal("Table", round.ExtraInfo["type"]);
        Assert.True(round.MustInvalidate);
    }

    [Fact]
    public void Varchar_IsWrittenInTheDuckDb20Layout()
    {
        DuckDbChunk chunk = ChunkBuilder.OneColumn(VarcharType, new VarBytesColumn
        {
            Type = VarcharType,
            Count = 3,
            Validity = new ValidityMask(new byte[] { 0b101, 0, 0, 0, 0, 0, 0, 0 }),
            Values = [(ReadOnlyMemory<byte>)"hello"u8.ToArray(), null, (ReadOnlyMemory<byte>)""u8.ToArray()],
        });
        ArrayBufferWriter<byte> buffer = new();
        DataChunkWriter.Write(new BinarySerializer(buffer), chunk);

        // The column object: 100 has_validity, 101 validity, then 107 / 108 / 109 instead of 102.
        BinaryDeserializer d = new(buffer.WrittenMemory);
        d.BeginObject();
        d.BeginProperty(fieldId: 100);
        _ = d.ReadUInt32();
        d.BeginProperty(fieldId: 101);
        ulong types = d.BeginList();
        for (ulong i = 0; i < types; i++) _ = LogicalType.Deserialize(d);
        d.EndList();
        d.BeginProperty(fieldId: 102);
        Assert.Equal(1UL, d.BeginList());
        d.BeginObject();
        d.BeginProperty(fieldId: 100);
        Assert.True(d.ReadBool());
        d.BeginProperty(fieldId: 101);
        _ = d.ReadDataMemory();
        d.BeginProperty(fieldId: 107);
        Assert.Equal(5UL, d.ReadUInt64());
        d.BeginProperty(fieldId: 108);
        byte[] lengths = d.ReadDataMemory().ToArray();
        Assert.Equal(new uint[] { 5, 0, 0 }, Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(lengths.AsSpan(i * 4))).ToArray());
        d.BeginProperty(fieldId: 109);
        Assert.Equal("hello"u8.ToArray(), d.ReadDataMemory().ToArray());

        DuckDbChunk round = DataChunkReader.Read(new BinaryDeserializer(buffer.WrittenMemory));
        VarBytesColumn col = Assert.IsType<VarBytesColumn>(round.Columns[0]);
        Assert.Equal("hello"u8.ToArray(), col.Values[0]!.Value.ToArray());
        Assert.Null(col.Values[1]);
        Assert.Empty(col.Values[2]!.Value.ToArray());
    }

    [Fact]
    public void Varchar_LegacyListLayout_IsStillRead()
    {
        ArrayBufferWriter<byte> buffer = new();
        BinarySerializer s = new(buffer);
        s.BeginObject();
        s.WriteProperty(fieldId: 100, 2u);
        s.WriteFieldId(101);
        s.BeginList(1);
        s.BeginObject();
        VarcharType.Serialize(s);
        s.EndObject();
        s.EndList();
        s.WriteFieldId(102);
        s.BeginList(1);
        s.BeginObject();
        s.WriteProperty(fieldId: 100, false);
        s.WriteFieldId(102);
        s.BeginList(2);
        s.WriteBlob("x"u8);
        s.WriteBlob("yz"u8);
        s.EndList();
        s.EndObject();
        s.EndList();
        s.EndObject();

        DuckDbChunk round = DataChunkReader.Read(new BinaryDeserializer(buffer.WrittenMemory));
        VarBytesColumn col = Assert.IsType<VarBytesColumn>(round.Columns[0]);
        Assert.Equal("yz"u8.ToArray(), col.Values[1]!.Value.ToArray());
    }

    [Fact]
    public void AppendSql_UsesTheDataStreamScan()
    {
        LogicalType dec = new(LogicalTypeId.Decimal, new DecimalTypeInfo { Width = 18, Scale = 3 });
        string sql = QuackConnection.BuildAppendSql("my schema", "t\"1", "abc", [IntType, VarcharType, dec]);
        Assert.Equal(
            "INSERT INTO \"my schema\".\"t\"\"1\" SELECT * FROM scan_data_from_quack_client('abc', "
            + "NULL::STRUCT(col0 INTEGER, col1 VARCHAR, col2 DECIMAL(18, 3)), ordered := true)",
            sql);
    }

    [Fact]
    public void TypeSql_RendersNestedTypes()
    {
        LogicalType list = new(LogicalTypeId.List, new ListTypeInfo { ChildType = IntType });
        LogicalType st = new(LogicalTypeId.Struct, new StructTypeInfo
        {
            ChildTypes = [new("a b", list), new("c", new LogicalType(LogicalTypeId.TimestampTzNs))],
        });
        LogicalType en = new(LogicalTypeId.Enum, new EnumTypeInfo { Values = ["x", "it's"] });
        LogicalType arr = new(LogicalTypeId.Array, new ArrayTypeInfo { ChildType = en, Size = 2 });
        Assert.Equal("STRUCT(\"a b\" INTEGER[], \"c\" TIMESTAMPTZ_NS)", LogicalTypeSql.ToSql(st));
        Assert.Equal("ENUM('x', 'it''s')[2]", LogicalTypeSql.ToSql(arr));
    }

    [Fact]
    public void NewTypeIds_MapToPhysicalTypes()
    {
        Assert.Equal(PhysicalType.Int64, new LogicalType(LogicalTypeId.TimestampTzNs).GetPhysicalType());
        Assert.Equal(PhysicalType.Struct, new LogicalType(LogicalTypeId.Tuple).GetPhysicalType());
        Assert.Equal((byte)33, (byte)LogicalTypeId.TimestampTzNs);
        Assert.Equal((byte)110, (byte)LogicalTypeId.Tuple);
    }
}
