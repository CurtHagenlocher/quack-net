using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Arrays;
using Apache.Arrow.Ipc;
using Apache.Arrow.Scalars;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// Exercises every DuckDB type we map to Arrow. Each test executes a SELECT
// that returns a single row containing a literal of the target type and
// verifies the Arrow column type + decoded value. Grouped by category
// rather than per type so the SELECT round-trip cost (one query per Fact)
// stays manageable.
public class TypeConversionTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public TypeConversionTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task SignedAndUnsignedIntegers_Roundtrip()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery =
            "SELECT CAST(-7 AS TINYINT) AS i1, CAST(-12345 AS SMALLINT) AS i2, " +
            "       CAST(42 AS INTEGER) AS i4, CAST(9223372036854775000 AS BIGINT) AS i8, " +
            "       CAST(250 AS UTINYINT) AS u1, CAST(65000 AS USMALLINT) AS u2, " +
            "       CAST(4294967290 AS UINTEGER) AS u4, CAST(18446744073709551610 AS UBIGINT) AS u8";

        RecordBatch batch = await ReadFirstBatch(stmt);
        Assert.Equal(1, batch.Length);
        Assert.Equal((sbyte)-7, Assert.IsType<Int8Array>(batch.Column(0)).GetValue(0));
        Assert.Equal((short)-12345, Assert.IsType<Int16Array>(batch.Column(1)).GetValue(0));
        Assert.Equal(42, Assert.IsType<Int32Array>(batch.Column(2)).GetValue(0));
        Assert.Equal(9223372036854775000L, Assert.IsType<Int64Array>(batch.Column(3)).GetValue(0));
        Assert.Equal((byte)250, Assert.IsType<UInt8Array>(batch.Column(4)).GetValue(0));
        Assert.Equal((ushort)65000, Assert.IsType<UInt16Array>(batch.Column(5)).GetValue(0));
        Assert.Equal(4294967290U, Assert.IsType<UInt32Array>(batch.Column(6)).GetValue(0));
        Assert.Equal(18446744073709551610UL, Assert.IsType<UInt64Array>(batch.Column(7)).GetValue(0));
    }

    [Fact]
    public async Task FloatAndDouble_Roundtrip()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT CAST(1.5 AS FLOAT) AS f, CAST(2.75 AS DOUBLE) AS d";

        RecordBatch batch = await ReadFirstBatch(stmt);
        Assert.Equal(1.5f, Assert.IsType<FloatArray>(batch.Column(0)).GetValue(0));
        Assert.Equal(2.75, Assert.IsType<DoubleArray>(batch.Column(1)).GetValue(0));
    }

    [Fact]
    public async Task Boolean_RoundtripWithBitPacking()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery =
            "SELECT b FROM (VALUES (TRUE), (FALSE), (TRUE), (TRUE), (FALSE), (NULL), (TRUE), (FALSE), (TRUE)) t(b)";

        RecordBatch batch = await ReadFirstBatch(stmt);
        BooleanArray col = Assert.IsType<BooleanArray>(batch.Column(0));
        Assert.Equal(9, col.Length);
        Assert.True(col.GetValue(0));
        Assert.False(col.GetValue(1));
        Assert.True(col.GetValue(2));
        Assert.True(col.GetValue(3));
        Assert.False(col.GetValue(4));
        Assert.Null(col.GetValue(5));
        Assert.True(col.GetValue(6));
        Assert.False(col.GetValue(7));
        Assert.True(col.GetValue(8));
    }

    [Fact]
    public async Task Date_Roundtrip()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT DATE '2026-05-13' AS d";

        RecordBatch batch = await ReadFirstBatch(stmt);
        Date32Array col = Assert.IsType<Date32Array>(batch.Column(0));
        Assert.Equal(new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Unspecified), col.GetDateTime(0));
    }

    [Fact]
    public async Task HugeIntAndUHugeInt_Roundtrip()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery =
            "SELECT CAST(12345678901234567890123456789 AS HUGEINT) AS h, " +
            "       CAST(12345678901234567890123456789 AS UHUGEINT) AS u";

        RecordBatch batch = await ReadFirstBatch(stmt);
        Decimal128Array h = Assert.IsType<Decimal128Array>(batch.Column(0));
        FixedSizeBinaryArray u = Assert.IsType<FixedSizeBinaryArray>(batch.Column(1));

        Assert.Equal(38, ((Decimal128Type)h.Data.DataType).Precision);
        Assert.Equal(16, ((FixedSizeBinaryType)u.Data.DataType).ByteWidth);

        Assert.Equal("12345678901234567890123456789", h.GetString(0));

        // UHUGEINT comes back as raw 16 bytes; verify the bit pattern.
        // DuckDB stores it lower-first, so the 16 bytes match LE encoding.
        ReadOnlySpan<byte> bytes = u.GetBytes(0);
        UInt128 reconstructed = (UInt128)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8])
            | ((UInt128)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8)) << 64);
        Assert.Equal(UInt128.Parse("12345678901234567890123456789"), reconstructed);
    }

    [Fact]
    public async Task Uuid_RoundtripAsFixedSizeBinary16()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT '12345678-1234-1234-1234-123456789abc'::UUID AS u";

        RecordBatch batch = await ReadFirstBatch(stmt);
        FixedSizeBinaryArray col = Assert.IsType<FixedSizeBinaryArray>(batch.Column(0));
        Assert.Equal(16, col.GetBytes(0).Length);
    }

    [Theory]
    [InlineData(4, 2, "12.34")]
    [InlineData(9, 4, "12345.6789")]
    [InlineData(18, 6, "123456789.123456")]
    [InlineData(38, 10, "12345678901234567890.1234567890")]
    public async Task Decimal_RoundtripAcrossAllBackingWidths(byte width, byte scale, string value)
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = $"SELECT CAST('{value}' AS DECIMAL({width}, {scale})) AS d";

        RecordBatch batch = await ReadFirstBatch(stmt);
        Decimal128Array col = Assert.IsType<Decimal128Array>(batch.Column(0));
        Decimal128Type type = Assert.IsType<Decimal128Type>(col.Data.DataType);
        Assert.Equal(width, type.Precision);
        Assert.Equal(scale, type.Scale);
        Assert.Equal(value, col.GetString(0));
    }

    [Fact]
    public async Task Time_RoundtripAsTime64Microsecond()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT TIME '12:34:56.789' AS t";

        RecordBatch batch = await ReadFirstBatch(stmt);
        Time64Array col = Assert.IsType<Time64Array>(batch.Column(0));
        Time64Type type = (Time64Type)col.Data.DataType;
        Assert.Equal(TimeUnit.Microsecond, type.Unit);
        // 12:34:56.789 = 45_296_789_000 micros (12h*3600 + 34*60 + 56 = 45296 seconds, .789 = 789000 micros).
        Assert.Equal(45_296_789_000L, col.GetValue(0));
    }

    [Fact]
    public async Task Timestamp_RoundtripWithMicrosecondUnit()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT TIMESTAMP '2026-05-13 12:34:56' AS ts";

        RecordBatch batch = await ReadFirstBatch(stmt);
        TimestampArray col = Assert.IsType<TimestampArray>(batch.Column(0));
        TimestampType type = (TimestampType)col.Data.DataType;
        Assert.Equal(TimeUnit.Microsecond, type.Unit);
        Assert.Null(type.Timezone);
        Assert.Equal(new DateTimeOffset(2026, 5, 13, 12, 34, 56, TimeSpan.Zero), col.GetTimestamp(0));
    }

    [Fact]
    public async Task TimestampTz_HasUtcTimezone()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT '2026-05-13 12:34:56+02:00'::TIMESTAMPTZ AS tz";

        RecordBatch batch = await ReadFirstBatch(stmt);
        TimestampArray col = Assert.IsType<TimestampArray>(batch.Column(0));
        TimestampType type = (TimestampType)col.Data.DataType;
        Assert.Equal("UTC", type.Timezone);
    }

    [Fact]
    public async Task TimeTz_AsStructWithMicrosAndOffset()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT '12:34:56+02'::TIMETZ AS t";

        RecordBatch batch = await ReadFirstBatch(stmt);
        StructArray col = Assert.IsType<StructArray>(batch.Column(0));
        Int64Array micros = Assert.IsType<Int64Array>(col.Fields[0]);
        Int32Array offset = Assert.IsType<Int32Array>(col.Fields[1]);
        Assert.Equal(45_296_000_000L, micros.GetValue(0));
        Assert.Equal(7200, offset.GetValue(0));
    }

    [Fact]
    public async Task Interval_AsMonthDayNanosecond()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT INTERVAL '1 year 2 months 3 days 04:05:06.789' AS iv";

        RecordBatch batch = await ReadFirstBatch(stmt);
        MonthDayNanosecondIntervalArray col = Assert.IsType<MonthDayNanosecondIntervalArray>(batch.Column(0));
        MonthDayNanosecondInterval iv = col.GetValue(0)!.Value;
        Assert.Equal(14, iv.Months);   // 1 year * 12 + 2 months
        Assert.Equal(3, iv.Days);
        // 04:05:06.789 = (4h*3600 + 5*60 + 6) seconds + 789 ms = 14706.789 s = 14_706_789_000 micros
        // converted to nanoseconds: * 1000 = 14_706_789_000_000.
        Assert.Equal(14_706_789_000_000L, iv.Nanoseconds);
    }

    [Fact]
    public async Task Blob_RoundtripsAsBinaryArray()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = @"SELECT '\xDE\xAD\xBE\xEF'::BLOB AS b";

        RecordBatch batch = await ReadFirstBatch(stmt);
        BinaryArray col = Assert.IsType<BinaryArray>(batch.Column(0));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, col.GetBytes(0).ToArray());
    }

    [Fact]
    public async Task Bit_RoundtripsAsBinaryArray()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT BIT '1010' AS b";

        RecordBatch batch = await ReadFirstBatch(stmt);
        BinaryArray col = Assert.IsType<BinaryArray>(batch.Column(0));
        Assert.Equal(new byte[] { 0x04, 0xFA }, col.GetBytes(0).ToArray());
    }

    [Fact]
    public async Task Geometry_RoundtripsAsBinaryWkb()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT ST_GeomFromText('POINT(1.5 2.5)') AS g";

        RecordBatch batch = await ReadFirstBatch(stmt);
        BinaryArray col = Assert.IsType<BinaryArray>(batch.Column(0));
        ReadOnlySpan<byte> bytes = col.GetBytes(0);
        Assert.Equal(21, bytes.Length); // WKB POINT: 1 (endian) + 4 (type) + 8 + 8 (XY)
        Assert.Equal(0x01, bytes[0]);   // little-endian byte order marker
    }

    [Fact]
    public async Task Enum_RoundtripsAsDictionaryArray()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery =
            "SELECT v FROM (VALUES " +
            "  ('happy'::ENUM('happy', 'sad', 'meh')), " +
            "  ('meh'::ENUM('happy', 'sad', 'meh')), " +
            "  ('sad'::ENUM('happy', 'sad', 'meh'))) t(v)";

        RecordBatch batch = await ReadFirstBatch(stmt);
        DictionaryArray col = Assert.IsType<DictionaryArray>(batch.Column(0));
        StringArray dict = Assert.IsType<StringArray>(col.Dictionary);
        Assert.Equal("happy", dict.GetString(0));
        Assert.Equal("sad", dict.GetString(1));
        Assert.Equal("meh", dict.GetString(2));

        UInt8Array indices = Assert.IsType<UInt8Array>(col.Indices);
        Assert.Equal((byte)0, indices.GetValue(0));
        Assert.Equal((byte)2, indices.GetValue(1));
        Assert.Equal((byte)1, indices.GetValue(2));
    }

    [Fact]
    public async Task List_RoundtripsAsListArray()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT [10, 20, 30]::INTEGER[] AS xs";

        RecordBatch batch = await ReadFirstBatch(stmt);
        ListArray col = Assert.IsType<ListArray>(batch.Column(0));
        Int32Array child = Assert.IsType<Int32Array>(col.Values);
        Assert.Equal(10, child.GetValue(0));
        Assert.Equal(20, child.GetValue(1));
        Assert.Equal(30, child.GetValue(2));
        // row 0 spans offsets [0, 3)
        Assert.Equal(0, col.ValueOffsets[0]);
        Assert.Equal(3, col.ValueOffsets[1]);
    }

    [Fact]
    public async Task Struct_RoundtripsAsStructArray()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT {'a': 42, 'b': 'hello'} AS s";

        RecordBatch batch = await ReadFirstBatch(stmt);
        StructArray col = Assert.IsType<StructArray>(batch.Column(0));
        Int32Array a = Assert.IsType<Int32Array>(col.Fields[0]);
        StringArray b = Assert.IsType<StringArray>(col.Fields[1]);
        Assert.Equal(42, a.GetValue(0));
        Assert.Equal("hello", b.GetString(0));
    }

    [Fact]
    public async Task FixedSizeArray_RoundtripsAsFixedSizeList()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT [1, 2, 3]::INTEGER[3] AS arr";

        RecordBatch batch = await ReadFirstBatch(stmt);
        FixedSizeListArray col = Assert.IsType<FixedSizeListArray>(batch.Column(0));
        Assert.Equal(3, ((FixedSizeListType)col.Data.DataType).ListSize);
        Int32Array child = Assert.IsType<Int32Array>(col.Values);
        Assert.Equal(1, child.GetValue(0));
        Assert.Equal(2, child.GetValue(1));
        Assert.Equal(3, child.GetValue(2));
    }

    [Fact]
    public async Task Map_RoundtripsAsMapArray()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT MAP(['a', 'b'], [1, 2]) AS m";

        RecordBatch batch = await ReadFirstBatch(stmt);
        MapArray col = Assert.IsType<MapArray>(batch.Column(0));
        StringArray keys = Assert.IsType<StringArray>(col.Keys);
        Int32Array values = Assert.IsType<Int32Array>(col.Values);
        Assert.Equal("a", keys.GetString(0));
        Assert.Equal("b", keys.GetString(1));
        Assert.Equal(1, values.GetValue(0));
        Assert.Equal(2, values.GetValue(1));
    }

    private AdbcConnection OpenConnection()
    {
        QuackAdbcDriver driver = new();
        AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
        });
        return db.Connect(options: null);
    }

    private static async Task<RecordBatch> ReadFirstBatch(AdbcStatement stmt)
    {
        QueryResult result = stmt.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        return batch;
    }
}
