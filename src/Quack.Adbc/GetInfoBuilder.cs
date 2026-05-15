using System.Reflection;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Quack.Adbc;

// Builds an ADBC GetInfo IArrowArrayStream — a stream of (info_name, info_value)
// rows where info_value is a dense union over six concrete value types
// (string / bool / int64 / int32-bitmask / list<utf8> / map<int32, list<int32>>).
//
// Schema is defined by ADBC:
//   info_name  : uint32 NOT NULL
//   info_value : dense_union<
//                  string_value : utf8,             type_id 0
//                  bool_value : bool,               type_id 1
//                  int64_value : int64,             type_id 2
//                  int32_bitmask : int32,           type_id 3
//                  string_list : list<utf8>,        type_id 4
//                  int32_to_int32_list_map : map<int32, list<int32>>   type_id 5
//                >
//
// Today every value we emit lands in the string or bool child; the other
// children are present-but-empty so the schema is well-formed.
internal static class GetInfoBuilder
{
    private static readonly Field StringField = new(
        "string_value", StringType.Default, nullable: true);
    private static readonly Field BoolField = new(
        "bool_value", BooleanType.Default, nullable: true);
    private static readonly Field Int64Field = new(
        "int64_value", Int64Type.Default, nullable: true);
    private static readonly Field Int32BitmaskField = new(
        "int32_bitmask", Int32Type.Default, nullable: true);
    private static readonly Field StringListField = new(
        "string_list",
        new ListType(new Field("item", StringType.Default, nullable: true)),
        nullable: true);
    private static readonly MapType Int32MapType = new(
        new Field("key", Int32Type.Default, nullable: false),
        new Field("value", new ListType(new Field("item", Int32Type.Default, nullable: true)), nullable: true),
        keySorted: false);
    private static readonly Field Int32MapField = new(
        "int32_to_int32_list_map", Int32MapType, nullable: true);

    private static readonly UnionType ValueUnion = new(
        new[] { StringField, BoolField, Int64Field, Int32BitmaskField, StringListField, Int32MapField },
        new[] { 0, 1, 2, 3, 4, 5 },
        UnionMode.Dense);

    private static readonly Schema GetInfoSchema = new Schema.Builder()
        .Field(new Field("info_name", UInt32Type.Default, nullable: false))
        .Field(new Field("info_value", ValueUnion, nullable: true))
        .Build();

    public static IArrowArrayStream Build(QuackAdbcConnection connection, IReadOnlyList<AdbcInfoCode>? codes)
    {
        Dictionary<AdbcInfoCode, object> all = BuildValues(connection);

        // The C ADBC contract says "info_codes_length == 0 means return all
        // codes." The C# binding's CAdbcDriverExporter loses the null
        // distinction (always allocates an array) so we treat empty the same
        // as null here.
        IEnumerable<AdbcInfoCode> requested = (codes is null || codes.Count == 0)
            ? all.Keys
            : codes.Where(all.ContainsKey);

        StringArray.Builder strings = new();
        BooleanArray.Builder bools = new();
        Int64Array.Builder int64s = new();
        Int32Array.Builder int32s = new();

        UInt32Array.Builder infoNames = new();
        ArrowBuffer.Builder<byte> typeIds = new();
        ArrowBuffer.Builder<int> valueOffsets = new();
        int length = 0;

        foreach (AdbcInfoCode code in requested)
        {
            object value = all[code];
            infoNames.Append((uint)code);
            switch (value)
            {
                case string s:
                    typeIds.Append((byte)0);
                    valueOffsets.Append(strings.Length);
                    strings.Append(s);
                    break;
                case bool b:
                    typeIds.Append((byte)1);
                    valueOffsets.Append(bools.Length);
                    bools.Append(b);
                    break;
                case long l:
                    typeIds.Append((byte)2);
                    valueOffsets.Append(int64s.Length);
                    int64s.Append(l);
                    break;
                case int i:
                    typeIds.Append((byte)3);
                    valueOffsets.Append(int32s.Length);
                    int32s.Append(i);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"GetInfo value for {code} has unsupported runtime type {value.GetType()}.");
            }
            length++;
        }

        IArrowArray[] children =
        {
            strings.Build(),
            bools.Build(),
            int64s.Build(),
            int32s.Build(),
            BuildEmptyStringList(),
            BuildEmptyInt32Map(),
        };

        DenseUnionArray valueArr = new(
            ValueUnion,
            length,
            children,
            typeIds.Build(),
            valueOffsets.Build(),
            nullCount: 0,
            offset: 0);

        RecordBatch batch = new(
            GetInfoSchema,
            new IArrowArray[] { infoNames.Build(), valueArr },
            length);

        return new SingleBatchArrayStream(GetInfoSchema, batch);
    }

    private static Dictionary<AdbcInfoCode, object> BuildValues(QuackAdbcConnection connection)
    {
        Assembly driverAsm = typeof(GetInfoBuilder).Assembly;
        Assembly arrowAsm = typeof(RecordBatch).Assembly;
        Assembly adbcAsm = typeof(AdbcConnection).Assembly;
        return new Dictionary<AdbcInfoCode, object>
        {
            [AdbcInfoCode.VendorName] = "DuckDB",
            [AdbcInfoCode.VendorVersion] = connection.Underlying.ServerDuckDbVersion,
            [AdbcInfoCode.VendorArrowVersion] = AssemblyVersion(arrowAsm),
            [AdbcInfoCode.VendorSql] = true,
            [AdbcInfoCode.VendorSubstrait] = false,
            [AdbcInfoCode.DriverName] = "quack-net ADBC",
            [AdbcInfoCode.DriverVersion] = AssemblyVersion(driverAsm),
            [AdbcInfoCode.DriverArrowVersion] = AssemblyVersion(arrowAsm),
            [AdbcInfoCode.DriverAdbcVersion] = AssemblyVersion(adbcAsm),
        };
    }

    private static string AssemblyVersion(Assembly asm) =>
        asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? asm.GetName().Version?.ToString()
        ?? "unknown";

    private static ListArray BuildEmptyStringList()
    {
        StringArray emptyValues = new StringArray.Builder().Build();
        ArrowBuffer offsets = new ArrowBuffer.Builder<int>(1).Append(0).Build();
        ArrayData data = new(
            StringListField.DataType,
            length: 0,
            nullCount: 0,
            offset: 0,
            buffers: new[] { ArrowBuffer.Empty, offsets },
            children: new[] { emptyValues.Data });
        return new ListArray(data);
    }

    private static MapArray BuildEmptyInt32Map()
    {
        // map<int32, list<int32>> = list<struct{key:int32, value:list<int32>}>.
        Int32Array emptyKeys = new Int32Array.Builder().Build();
        Int32Array emptyListValues = new Int32Array.Builder().Build();
        ArrowBuffer emptyListOffsets = new ArrowBuffer.Builder<int>(1).Append(0).Build();
        IArrowType valueListType = Int32MapType.KeyValueType.Fields[1].DataType;
        ArrayData emptyValueList = new(
            valueListType,
            length: 0,
            nullCount: 0,
            offset: 0,
            buffers: new[] { ArrowBuffer.Empty, emptyListOffsets },
            children: new[] { emptyListValues.Data });
        ArrayData entries = new(
            Int32MapType.KeyValueType,
            length: 0,
            nullCount: 0,
            offset: 0,
            buffers: new[] { ArrowBuffer.Empty },
            children: new[] { emptyKeys.Data, emptyValueList });
        ArrowBuffer mapOffsets = new ArrowBuffer.Builder<int>(1).Append(0).Build();
        ArrayData mapData = new(
            Int32MapType,
            length: 0,
            nullCount: 0,
            offset: 0,
            buffers: new[] { ArrowBuffer.Empty, mapOffsets },
            children: new[] { entries });
        return new MapArray(mapData);
    }

    private sealed class SingleBatchArrayStream : IArrowArrayStream
    {
        private readonly Schema _schema;
        private RecordBatch? _batch;

        public SingleBatchArrayStream(Schema schema, RecordBatch batch)
        {
            _schema = schema;
            _batch = batch;
        }

        public Schema Schema => _schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            RecordBatch? next = _batch;
            _batch = null;
            return ValueTask.FromResult(next);
        }

        public void Dispose()
        {
            _batch?.Dispose();
            _batch = null;
        }
    }
}
