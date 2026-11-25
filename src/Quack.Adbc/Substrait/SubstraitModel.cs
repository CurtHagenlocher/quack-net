// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
//
// Derived from kusto_adbc (src/KustoAdbc/Substrait/SubstraitModel.cs);
// see THIRD-PARTY-NOTICES.md. Changes: namespace; virtual tables, grouping
// sets, ordered aggregates, WITH TIES, subqueries and outer references.

namespace Quack.Adbc.Substrait;

// A decoded Substrait plan, limited to the constructs this driver can translate.
// The decoder (SubstraitPlanDecoder) rejects anything that cannot be
// represented here, so the T-SQL emitter only has to deal with this model.

#region Types

enum TypeKind
{
    Unknown,
    Bool,
    I8,
    I16,
    I32,
    I64,
    Fp32,
    Fp64,
    String,
    Binary,
    Date,
    IntervalYear,
    IntervalDay,
    IntervalCompound,
    Uuid,
    FixedChar,
    VarChar,
    FixedBinary,
    Decimal,
    PrecisionTime,
    PrecisionTimestamp,
    PrecisionTimestampTz,
    Struct,
    List,
    Map,
    Func,
    Unbound,
    UserDefined,
    Alias,
}

/// <summary>A (shallow) Substrait type. Nested types only record their kind.</summary>
readonly struct SType : IEquatable<SType>
{
    public readonly TypeKind Kind;
    public readonly bool Nullable;
    /// <summary>Precision for decimal/precision-time types; length for char/binary types.</summary>
    public readonly int Precision;
    public readonly int Scale;

    public SType(TypeKind kind, bool nullable = true, int precision = 0, int scale = 0)
    {
        Kind = kind;
        Nullable = nullable;
        Precision = precision;
        Scale = scale;
    }

    public static readonly SType Unknown = new(TypeKind.Unknown);
    public static readonly SType Bool = new(TypeKind.Bool);
    public static readonly SType I32 = new(TypeKind.I32);
    public static readonly SType I64 = new(TypeKind.I64);
    public static readonly SType Fp64 = new(TypeKind.Fp64);
    public static readonly SType String = new(TypeKind.String);

    public bool IsKnown => Kind != TypeKind.Unknown;

    public bool IsStringLike => Kind is TypeKind.String or TypeKind.VarChar or TypeKind.FixedChar;
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] binary and fixed binary.</summary>
    public bool IsBinaryLike => Kind is TypeKind.Binary or TypeKind.FixedBinary;

    public bool IsInteger => Kind is TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64;

    public bool IsFloat => Kind is TypeKind.Fp32 or TypeKind.Fp64;

    public bool IsTimestamp => Kind is TypeKind.PrecisionTimestamp or TypeKind.PrecisionTimestampTz or TypeKind.Date;

    /// <summary>The token used for this type in a function signature compound name.</summary>
    public string? SignatureToken => Kind switch
    {
        TypeKind.Bool => "bool",
        TypeKind.I8 => "i8",
        TypeKind.I16 => "i16",
        TypeKind.I32 => "i32",
        TypeKind.I64 => "i64",
        TypeKind.Fp32 => "fp32",
        TypeKind.Fp64 => "fp64",
        TypeKind.String => "str",
        TypeKind.Binary => "vbin",
        TypeKind.Date => "date",
        TypeKind.IntervalYear => "iyear",
        TypeKind.IntervalDay => "iday",
        TypeKind.IntervalCompound => "icompound",
        TypeKind.Uuid => "uuid",
        TypeKind.FixedChar => "fchar",
        TypeKind.VarChar => "vchar",
        TypeKind.FixedBinary => "fbin",
        TypeKind.Decimal => "dec",
        TypeKind.PrecisionTime => "pt",
        TypeKind.PrecisionTimestamp => "pts",
        TypeKind.PrecisionTimestampTz => "ptstz",
        TypeKind.Struct => "struct",
        TypeKind.List => "list",
        TypeKind.Map => "map",
        TypeKind.Func => "func",
        _ => null,
    };

    public SType WithNullable(bool nullable) => new(Kind, nullable, Precision, Scale);

    public bool Equals(SType other) => Kind == other.Kind && Precision == other.Precision && Scale == other.Scale;
    public override bool Equals(object? obj) => obj is SType t && Equals(t);
    public override int GetHashCode() => ((int)Kind * 397) ^ (Precision * 31) ^ Scale;
    public override string ToString() => Kind switch
    {
        TypeKind.Decimal => $"decimal<{Precision},{Scale}>",
        TypeKind.PrecisionTime or TypeKind.PrecisionTimestamp or TypeKind.PrecisionTimestampTz
            or TypeKind.VarChar or TypeKind.FixedChar or TypeKind.FixedBinary => $"{SignatureToken}<{Precision}>",
        _ => SignatureToken ?? Kind.ToString(),
    };
}

#endregion

#region Functions

/// <summary>A function declared in the plan's extension section.</summary>
sealed class FunctionDecl
{
    /// <summary>Normalized extension URN, or null when the plan did not name one.</summary>
    public string? Urn;
    /// <summary>The function name without its signature.</summary>
    public string Name = "";
    /// <summary>The signature part of a compound name ("i64_i64"), or null for a bare name.</summary>
    public string? Signature;
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] The name as declared, unsplit (a catalog function's name is a JSON
    /// array of its schema path and name, which may contain a colon).</summary>
    public string RawName = "";

    public string DisplayName => Signature == null ? Name : Name + ":" + Signature;
}

sealed class FunctionArg
{
    public Expr? Value;
    public string? Enum;
    public SType? TypeArg;
}

sealed class FunctionOptionValue
{
    public string Name = "";
    public List<string> Preference = new();
}

#endregion

#region Expressions

abstract class Expr
{
}

sealed class LiteralExpr : Expr
{
    public SType Type;
    public bool IsNull;
    public bool BoolValue;
    public long IntValue;           // integers, date (days), time/timestamp values
    public double FloatValue;
    public string? StringValue;
    public byte[]? BytesValue;      // uuid, decimal (16-byte little-endian two's complement), binary
    // interval_day_to_second
    public long Days;
    public long Seconds;
    public long Subseconds;
    public int SubsecondPrecision;
    // interval_year_to_month
    public long Years;
    public long Months;
}

sealed class FieldRefExpr : Expr
{
    public int Index;
    /// <summary>Subquery boundaries to cross (an outer reference), or 0 for the current input.</summary>
    public int StepsOut;
}

sealed class ScalarFunctionExpr : Expr
{
    public FunctionDecl Function = null!;
    public List<FunctionArg> Args = new();
    public List<FunctionOptionValue> Options = new();
    public SType? OutputType;
}

sealed class IfThenExpr : Expr
{
    public List<(Expr If, Expr Then)> Clauses = new();
    public Expr? Else;
}

sealed class SwitchExpr : Expr
{
    public Expr Match = null!;
    public List<(LiteralExpr If, Expr Then)> Cases = new();
    public Expr? Else;
}

sealed class SingularOrListExpr : Expr
{
    public Expr Value = null!;
    public List<Expr> Options = new();
}

sealed class MultiOrListExpr : Expr
{
    public List<Expr> Values = new();
    public List<List<Expr>> Options = new();
}

sealed class CastExpr : Expr
{
    public SType Type;
    public Expr Input = null!;
    public int FailureBehavior;
}

sealed class ContextVariableExpr : Expr
{
    /// <summary>One of the SubstraitFields.ContextVariable_* constants.</summary>
    public int Variable;
}

enum SubqueryKind
{
    Scalar,
    In,
    Exists,
    Comparison,
}

sealed class SubqueryExpr : Expr
{
    public SubqueryKind Kind;
    public Rel Input = null!;
    /// <summary>The value tested by an IN predicate, or the left side of a comparison.</summary>
    public Expr? Needle;
    /// <summary>For comparisons: one of the SubstraitFields.ComparisonOp_* constants.</summary>
    public int ComparisonOp;
    /// <summary>For comparisons: one of the SubstraitFields.ReductionOp_* constants.</summary>
    public int ReductionOp;
}

sealed class AggregateCall
{
    public FunctionDecl Function = null!;
    public List<FunctionArg> Args = new();
    public List<FunctionOptionValue> Options = new();
    public SType? OutputType;
    public int Invocation;
    /// <summary>The order of values for ordered aggregates (e.g. string_agg).</summary>
    public List<SortField> Sorts = new();
}

sealed class Measure
{
    public AggregateCall Call = null!;
    public Expr? Filter;
}

sealed class SortField
{
    public Expr Expr = null!;
    public int Direction;
}

#endregion

#region Relations

abstract class Rel
{
    /// <summary>RelCommon.emit output mapping, or null for direct output.</summary>
    public List<int>? Emit;
}

sealed class ReadRel : Rel
{
    /// <summary>NamedTable names; empty for a virtual table.</summary>
    public List<string> TableNames = new();
    public List<string> ColumnNames = new();
    public List<SType> ColumnTypes = new();
    /// <summary>The rows of a virtual table (VALUES), or null for a named table.</summary>
    public List<List<Expr>>? VirtualRows;
    public Expr? Filter;
    public Expr? BestEffortFilter;
    public List<int>? Projection;
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] For a table-function read (<c>fabricator.table_function</c>): the
    /// arguments of the call, <see cref="TableNames"/> naming the function. Null for a table.</summary>
    public List<Expr>? TableFunctionArgs;
}

/// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] A write to a named table (only INSERT is translated).</summary>
sealed class WriteRel : Rel
{
    public List<string> TableNames = new();
    /// <summary>The columns written, in the input's column order; the table's other columns take their defaults.</summary>
    public List<string> ColumnNames = new();
    public List<SType> ColumnTypes = new();
    public int Op;
    public int Output;
    public int CreateMode;
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] The type URL of the rel's advanced-extension enhancement, which
    /// gives it a meaning the spec does not define (<c>fabricator.merge</c>), and its value.</summary>
    public string? Enhancement;
    public string EnhancementValue = "";
    public Rel Input = null!;
}

sealed class FilterRel : Rel
{
    public Rel Input = null!;
    public Expr Condition = null!;
}

sealed class ProjectRel : Rel
{
    public Rel Input = null!;
    public List<Expr> Expressions = new();
}

sealed class FetchRel : Rel
{
    public Rel Input = null!;
    public Expr? Offset;
    public Expr? Count;
}

sealed class SortRel : Rel
{
    public Rel Input = null!;
    public List<SortField> Sorts = new();
}

sealed class AggregateRel : Rel
{
    public Rel Input = null!;
    /// <summary>The grouping expressions that the grouping sets refer to.</summary>
    public List<Expr> GroupingExpressions = new();
    /// <summary>The grouping sets, as indexes into <see cref="GroupingExpressions"/>; empty for a global aggregate.</summary>
    public List<List<int>> GroupingSets = new();
    public List<Measure> Measures = new();
}

sealed class JoinRel : Rel
{
    public Rel Left = null!;
    public Rel Right = null!;
    public Expr? Expression;
    public Expr? PostJoinFilter;
    public int Type;
}

sealed class CrossRel : Rel
{
    public Rel Left = null!;
    public Rel Right = null!;
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] <c>fabricator.apply</c>: the right side is called per left row.</summary>
    public bool Apply;
}

sealed class SetRel : Rel
{
    public List<Rel> Inputs = new();
    public int Op;
}

sealed class TopNRel : Rel
{
    public Rel Input = null!;
    public List<SortField> Sorts = new();
    public Expr? Offset;
    public Expr? Count;
    public bool WithTies;
}

/// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] A window frame bound: its kind (a WindowBound_* field) and a row offset.</summary>
sealed class WindowBound
{
    public int Kind;
    public Expr? Offset;
}

/// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] A window function of a <see cref="WindowRel"/>.</summary>
sealed class WindowCall
{
    public FunctionDecl Function = null!;
    public List<FunctionArg> Args = new();
    public List<FunctionOptionValue> Options = new();
    public SType? OutputType;
    public int Invocation;
    public int BoundsType;
    public WindowBound? Lower;
    public WindowBound? Upper;
}

/// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] ConsistentPartitionWindowRel: the input's columns, then one per function.</summary>
sealed class WindowRel : Rel
{
    public Rel Input = null!;
    public List<WindowCall> Functions = new();
    public List<Expr> Partitions = new();
    public List<SortField> Sorts = new();
}

sealed class RootRel
{
    public Rel Input = null!;
    /// <summary>Output column names from RelRoot, or null for a bare Rel.</summary>
    public List<string>? Names;
}

#endregion
