// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
//
// Derived from kusto_adbc (src/KustoAdbc/Substrait/SubstraitFields.cs);
// see THIRD-PARTY-NOTICES.md. Changes: namespace; constants for mark joins,
// all set operations, WITH TIES, virtual tables, nested structs, subqueries
// and outer references.

namespace Quack.Adbc.Substrait;

/// <summary>
/// Named constants for Substrait protobuf field numbers.
/// These correspond to the field numbers defined in the Substrait .proto schema
/// (substrait-io/substrait v0.107, proto/substrait/*.proto) and are used in place
/// of raw numeric literals when parsing protobuf wire format.
/// Fields marked "legacy" are reserved in the current proto but are still
/// emitted by older producers, so they are accepted on input.
/// </summary>
internal static class SubstraitFields
{
    // ── Plan ───────────────────────────────────────────────────────
    public const int Plan_ExtensionUris = 1;            // legacy (SimpleExtensionURI)
    public const int Plan_ExtensionDeclarations = 2;
    public const int Plan_Relations = 3;
    public const int Plan_AdvancedExtensions = 4;
    public const int Plan_ExpectedTypeUrls = 5;
    public const int Plan_Version = 6;
    public const int Plan_ParameterBindings = 7;
    public const int Plan_ExtensionUrns = 8;
    public const int Plan_TypeAliases = 9;
    public const int Plan_ExecutionBehavior = 10;
    public const int Plan_NamedLambdas = 11;

    // ── ExecutionBehavior ──────────────────────────────────────────
    public const int ExecutionBehavior_VariableEvalMode = 1;

    // ── PlanRel ────────────────────────────────────────────────────
    public const int PlanRel_Rel = 1;
    public const int PlanRel_Root = 2;

    // ── RelRoot ────────────────────────────────────────────────────
    public const int RelRoot_Input = 1;
    public const int RelRoot_Names = 2;

    // ── Rel (oneof rel_type) ───────────────────────────────────────
    public const int Rel_Read = 1;
    public const int Rel_Filter = 2;
    public const int Rel_Fetch = 3;
    public const int Rel_Aggregate = 4;
    public const int Rel_Sort = 5;
    public const int Rel_Join = 6;
    public const int Rel_Project = 7;
    public const int Rel_Set = 8;
    public const int Rel_ExtensionSingle = 9;
    public const int Rel_ExtensionMulti = 10;
    public const int Rel_ExtensionLeaf = 11;
    public const int Rel_Cross = 12;
    public const int Rel_HashJoin = 13;
    public const int Rel_MergeJoin = 14;
    public const int Rel_Exchange = 15;
    public const int Rel_Expand = 16;
    public const int Rel_Window = 17;
    public const int Rel_NestedLoopJoin = 18;
    public const int Rel_Write = 19;
    public const int Rel_Ddl = 20;
    public const int Rel_Reference = 21;
    public const int Rel_Update = 22;
    public const int Rel_TopN = 23;
    public const int Rel_LateralJoin = 24;

    // ── RelCommon ──────────────────────────────────────────────────
    public const int RelCommon_Direct = 1;
    public const int RelCommon_Emit = 2;
    public const int RelCommon_Hint = 3;
    public const int RelCommon_AdvancedExtension = 4;
    public const int RelCommon_RelAnchor = 5;
    public const int Emit_OutputMapping = 1;

    // ── AdvancedExtension ──────────────────────────────────────────
    public const int AdvancedExtension_Optimization = 1;
    public const int AdvancedExtension_Enhancement = 2;

    // Every relation message uses this field number for advanced_extension
    // (TopNRel is the exception, see TopNRel_AdvancedExtension).
    public const int Rel_AdvancedExtension = 10;

    // ── ReadRel ────────────────────────────────────────────────────
    // ── WriteRel ([FABRICATOR-PATCH: OURS-BY-DESIGN]) ─────────────────
    public const int WriteRel_NamedTable = 1;
    public const int WriteRel_ExtensionTable = 2;
    public const int WriteRel_TableSchema = 3;
    public const int WriteRel_Op = 4;
    public const int WriteRel_Input = 5;
    public const int WriteRel_Output = 6;
    public const int WriteRel_Common = 7;
    public const int WriteRel_CreateMode = 8;
    public const int WriteRel_AdvancedExtension = 9;
    public const int WriteOp_Insert = 1;
    public const int WriteOp_Delete = 2;
    public const int WriteOp_Update = 3;
    public const int WriteOp_Ctas = 4;
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] Not a spec value: what TranslatePushdown reports for a WriteRel
    /// carrying the <c>fabricator.merge</c> enhancement.</summary>
    public const int WriteOp_FabricatorMerge = -1;
    public const string Enhancement_FabricatorMerge = "fabricator.merge";
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] On a ReadRel: the named table is a TABLE FUNCTION of the catalog,
    /// and the enhancement's value is an Expression.Nested.Struct of its arguments.</summary>
    public const string Enhancement_FabricatorTableFunction = "fabricator.table_function";
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] On a CrossRel: its right side is a table function called once per
    /// row of the left side; the function's arguments are field references into the LEFT input.</summary>
    public const string Enhancement_FabricatorApply = "fabricator.apply";
    public const int Any_TypeUrl = 1;
    public const int Any_Value = 2;
    public const int WriteOutput_Unspecified = 0;
    public const int WriteOutput_NoOutput = 1;
    public const int WriteOutput_ModifiedRecords = 2;

    public const int ReadRel_Common = 1;
    public const int ReadRel_BaseSchema = 2;
    public const int ReadRel_Filter = 3;
    public const int ReadRel_Projection = 4;
    public const int ReadRel_VirtualTable = 5;
    public const int ReadRel_LocalFiles = 6;
    public const int ReadRel_NamedTable = 7;
    public const int ReadRel_ExtensionTable = 8;
    public const int ReadRel_IcebergTable = 9;
    public const int ReadRel_BestEffortFilter = 11;

    // ── ReadRel.VirtualTable ───────────────────────────────────────
    public const int VirtualTable_Values = 1;           // legacy (Literal.Struct rows)
    public const int VirtualTable_Expressions = 2;

    // ── FilterRel ──────────────────────────────────────────────────
    public const int FilterRel_Common = 1;
    public const int FilterRel_Input = 2;
    public const int FilterRel_Condition = 3;

    // ── FetchRel ───────────────────────────────────────────────────
    public const int FetchRel_Common = 1;
    public const int FetchRel_Input = 2;
    public const int FetchRel_Offset = 3;               // legacy int64
    public const int FetchRel_Count = 4;                // legacy int64
    public const int FetchRel_OffsetExpr = 5;
    public const int FetchRel_CountExpr = 6;

    // ── ProjectRel ─────────────────────────────────────────────────
    public const int ProjectRel_Common = 1;
    public const int ProjectRel_Input = 2;
    public const int ProjectRel_Expressions = 3;

    // ── SortRel ────────────────────────────────────────────────────
    // [FABRICATOR-PATCH: OURS-BY-DESIGN] ConsistentPartitionWindowRel and its parts (algebra.proto)
    public const int WindowRel_Common = 1;
    public const int WindowRel_Input = 2;
    public const int WindowRel_WindowFunctions = 3;
    public const int WindowRel_PartitionExpressions = 4;
    public const int WindowRel_Sorts = 5;
    public const int WindowRelFunction_FunctionReference = 1;
    public const int WindowRelFunction_UpperBound = 4;
    public const int WindowRelFunction_LowerBound = 5;
    public const int WindowRelFunction_Phase = 6;
    public const int WindowRelFunction_OutputType = 7;
    public const int WindowRelFunction_Arguments = 9;
    public const int WindowRelFunction_Invocation = 10;
    public const int WindowRelFunction_Options = 11;
    public const int WindowRelFunction_BoundsType = 12;
    public const int WindowBound_Preceding = 1;
    public const int WindowBound_Following = 2;
    public const int WindowBound_CurrentRow = 3;
    public const int WindowBound_Unbounded = 4;
    public const int WindowBoundOffset_Offset = 1;
    public const int WindowBoundOffset_OffsetExpr = 2;
    public const int BoundsType_Unspecified = 0;
    public const int BoundsType_Rows = 1;
    public const int BoundsType_Range = 2;

    public const int SortRel_Common = 1;
    public const int SortRel_Input = 2;
    public const int SortRel_Sorts = 3;

    // ── AggregateRel ───────────────────────────────────────────────
    public const int AggregateRel_Common = 1;
    public const int AggregateRel_Input = 2;
    public const int AggregateRel_Groupings = 3;
    public const int AggregateRel_Measures = 4;
    public const int AggregateRel_GroupingExpressions = 5;

    // ── AggregateRel.Grouping ──────────────────────────────────────
    public const int Grouping_GroupingExpressions = 1;  // legacy inline expressions
    public const int Grouping_ExpressionReferences = 2;

    // ── AggregateRel.Measure ───────────────────────────────────────
    public const int Measure_Measure = 1;
    public const int Measure_Filter = 2;

    // ── AggregateFunction ──────────────────────────────────────────
    public const int AggregateFunction_FunctionReference = 1;
    public const int AggregateFunction_Args = 2;        // legacy
    public const int AggregateFunction_Sorts = 3;
    public const int AggregateFunction_Phase = 4;
    public const int AggregateFunction_OutputType = 5;
    public const int AggregateFunction_Invocation = 6;
    public const int AggregateFunction_Arguments = 7;
    public const int AggregateFunction_Options = 8;

    public const int AggregationPhase_Unspecified = 0;
    public const int AggregationPhase_InitialToResult = 3;
    public const int AggregationInvocation_Unspecified = 0;
    public const int AggregationInvocation_All = 1;
    public const int AggregationInvocation_Distinct = 2;

    // ── JoinRel ────────────────────────────────────────────────────
    public const int JoinRel_Common = 1;
    public const int JoinRel_Left = 2;
    public const int JoinRel_Right = 3;
    public const int JoinRel_Expression = 4;
    public const int JoinRel_PostJoinFilter = 5;
    public const int JoinRel_Type = 6;

    public const int JoinType_Unspecified = 0;
    public const int JoinType_Inner = 1;
    public const int JoinType_Outer = 2;
    public const int JoinType_Left = 3;
    public const int JoinType_Right = 4;
    public const int JoinType_LeftSemi = 5;
    public const int JoinType_LeftAnti = 6;
    public const int JoinType_LeftSingle = 7;
    public const int JoinType_RightSemi = 8;
    public const int JoinType_RightAnti = 9;
    public const int JoinType_RightSingle = 10;
    public const int JoinType_LeftMark = 11;
    public const int JoinType_RightMark = 12;

    // ── CrossRel ───────────────────────────────────────────────────
    public const int CrossRel_Common = 1;
    public const int CrossRel_Left = 2;
    public const int CrossRel_Right = 3;

    // ── SetRel ─────────────────────────────────────────────────────
    public const int SetRel_Common = 1;
    public const int SetRel_Inputs = 2;
    public const int SetRel_Op = 3;
    public const int SetOp_MinusPrimary = 1;
    public const int SetOp_MinusMultiset = 2;
    public const int SetOp_IntersectionPrimary = 3;
    public const int SetOp_IntersectionMultiset = 4;

    public const int SetOp_UnionDistinct = 5;
    public const int SetOp_UnionAll = 6;
    public const int SetOp_MinusPrimaryAll = 7;
    public const int SetOp_IntersectionMultisetAll = 8;

    // ── TopNRel ────────────────────────────────────────────────────
    public const int TopNRel_Common = 1;
    public const int TopNRel_Input = 2;
    public const int TopNRel_Sorts = 3;
    public const int TopNRel_Offset = 4;
    public const int TopNRel_Count = 5;
    public const int TopNRel_Mode = 6;
    public const int TopNRel_AdvancedExtension = 7;

    public const int FetchMode_Unspecified = 0;
    public const int FetchMode_RowsOnly = 1;
    public const int FetchMode_WithTies = 2;


    // ── SortField ──────────────────────────────────────────────────
    public const int SortField_Expr = 1;
    public const int SortField_Direction = 2;
    public const int SortField_ComparisonFunctionReference = 3;

    public const int SortDirection_Unspecified = 0;
    public const int SortDirection_AscNullsFirst = 1;
    public const int SortDirection_AscNullsLast = 2;
    public const int SortDirection_DescNullsFirst = 3;
    public const int SortDirection_DescNullsLast = 4;
    public const int SortDirection_Clustered = 5;

    // ── Expression (oneof rex_type) ────────────────────────────────
    public const int Expression_Literal = 1;
    public const int Expression_Selection = 2;
    public const int Expression_ScalarFunction = 3;
    public const int Expression_WindowFunction = 5;
    public const int Expression_IfThen = 6;
    public const int Expression_SwitchExpression = 7;
    public const int Expression_SingularOrList = 8;
    public const int Expression_MultiOrList = 9;
    public const int Expression_Cast = 11;
    public const int Expression_Subquery = 12;
    public const int Expression_Nested = 13;
    public const int Expression_DynamicParameter = 14;
    public const int Expression_Lambda = 15;
    public const int Expression_LambdaInvocation = 16;
    public const int Expression_ExecutionContextVariable = 17;
    public const int Expression_DetachedExpressionOrdinal = 18;
    public const int Expression_NamedLambdaReference = 19;

    // ── Expression.Literal (oneof literal_type) ────────────────────
    public const int Literal_Boolean = 1;
    public const int Literal_I8 = 2;
    public const int Literal_I16 = 3;
    public const int Literal_I32 = 5;
    public const int Literal_I64 = 7;
    public const int Literal_Fp32 = 10;
    public const int Literal_Fp64 = 11;
    public const int Literal_String = 12;
    public const int Literal_Binary = 13;
    public const int Literal_Date = 16;
    public const int Literal_IntervalYearToMonth = 19;
    public const int Literal_IntervalDayToSecond = 20;
    public const int Literal_FixedChar = 21;
    public const int Literal_VarChar = 22;
    public const int Literal_FixedBinary = 23;
    public const int Literal_Decimal = 24;
    public const int Literal_Struct = 25;
    public const int Literal_Map = 26;
    public const int Literal_Uuid = 28;
    public const int Literal_Null = 29;
    public const int Literal_List = 30;
    public const int Literal_EmptyList = 31;
    public const int Literal_EmptyMap = 32;
    public const int Literal_UserDefined = 33;
    public const int Literal_PrecisionTimestamp = 34;
    public const int Literal_PrecisionTimestampTz = 35;
    public const int Literal_IntervalCompound = 36;
    public const int Literal_PrecisionTime = 37;
    public const int Literal_Nullable = 50;
    public const int Literal_TypeVariationReference = 51;

    public const int VarCharLiteral_Value = 1;
    public const int DecimalLiteral_Value = 1;
    public const int DecimalLiteral_Precision = 2;
    public const int DecimalLiteral_Scale = 3;
    public const int PrecisionTimestampLiteral_Precision = 1;
    public const int PrecisionTimestampLiteral_Value = 2;
    public const int PrecisionTimeLiteral_Precision = 1;
    public const int PrecisionTimeLiteral_Value = 2;
    public const int IntervalYearToMonth_Years = 1;
    public const int IntervalYearToMonth_Months = 2;
    public const int IntervalDayToSecond_Days = 1;
    public const int IntervalDayToSecond_Seconds = 2;
    public const int IntervalDayToSecond_Microseconds = 3;   // legacy
    public const int IntervalDayToSecond_Precision = 4;
    public const int IntervalDayToSecond_Subseconds = 5;

    // ── FieldReference ─────────────────────────────────────────────
    public const int FieldReference_DirectReference = 1;
    public const int FieldReference_MaskedReference = 2;
    public const int FieldReference_Expression = 3;
    public const int FieldReference_RootReference = 4;
    public const int FieldReference_OuterReference = 5;
    public const int FieldReference_LambdaParameterReference = 6;

    // ── FieldReference.OuterReference (oneof) ──────────────────────
    public const int OuterReference_StepsOut = 1;
    public const int OuterReference_RelReference = 2;

    // ── ReferenceSegment (oneof reference_type) ────────────────────
    public const int ReferenceSegment_MapKey = 1;
    public const int ReferenceSegment_StructField = 2;
    public const int ReferenceSegment_ListElement = 3;

    // ── ReferenceSegment.StructField ───────────────────────────────
    public const int StructField_Field = 1;
    public const int StructField_Child = 2;

    // ── Expression.MaskExpression ──────────────────────────────────
    public const int MaskExpression_Select = 1;
    public const int MaskExpression_MaintainSingularStruct = 2;
    public const int StructSelect_StructItems = 1;
    public const int StructItem_Field = 1;
    public const int StructItem_Child = 2;

    // ── ScalarFunction ─────────────────────────────────────────────
    public const int ScalarFunction_FunctionReference = 1;
    public const int ScalarFunction_Args = 2;           // legacy
    public const int ScalarFunction_OutputType = 3;
    public const int ScalarFunction_Arguments = 4;
    public const int ScalarFunction_Options = 5;

    // ── FunctionArgument (oneof arg_type) ──────────────────────────
    public const int FunctionArgument_Enum = 1;
    public const int FunctionArgument_Type = 2;
    public const int FunctionArgument_Value = 3;

    // ── FunctionOption ─────────────────────────────────────────────
    public const int FunctionOption_Name = 1;
    public const int FunctionOption_Preference = 2;

    // ── IfThen ─────────────────────────────────────────────────────
    public const int IfThen_Ifs = 1;
    public const int IfThen_Else = 2;

    // ── IfClause ───────────────────────────────────────────────────
    public const int IfClause_If = 1;
    public const int IfClause_Then = 2;

    // ── SwitchExpression ───────────────────────────────────────────
    public const int Switch_Ifs = 1;
    public const int Switch_Else = 2;
    public const int Switch_Match = 3;
    public const int SwitchIfValue_If = 1;
    public const int SwitchIfValue_Then = 2;

    // ── SingularOrList / MultiOrList ───────────────────────────────
    public const int SingularOrList_Value = 1;
    public const int SingularOrList_Options = 2;
    public const int MultiOrList_Value = 1;
    public const int MultiOrList_Options = 2;
    public const int MultiOrListRecord_Fields = 1;

    // ── Expression.Nested / Literal.Struct ─────────────────────────
    public const int Nested_Nullable = 1;
    public const int Nested_TypeVariationReference = 2;
    public const int Nested_Struct = 3;
    public const int NestedStruct_Fields = 1;
    public const int LiteralStruct_Fields = 1;

    // ── Expression.Subquery (oneof subquery_type) ──────────────────
    public const int Subquery_Scalar = 1;
    public const int Subquery_InPredicate = 2;
    public const int Subquery_SetPredicate = 3;
    public const int Subquery_SetComparison = 4;
    public const int SubqueryScalar_Input = 1;
    public const int InPredicate_Needles = 1;
    public const int InPredicate_Haystack = 2;
    public const int SetPredicate_PredicateOp = 1;
    public const int SetPredicate_Tuples = 2;
    public const int PredicateOp_Exists = 1;
    public const int PredicateOp_Unique = 2;
    public const int SetComparison_ReductionOp = 1;
    public const int SetComparison_ComparisonOp = 2;
    public const int SetComparison_Left = 3;
    public const int SetComparison_Right = 4;
    public const int ComparisonOp_Eq = 1;
    public const int ComparisonOp_Ne = 2;
    public const int ComparisonOp_Lt = 3;
    public const int ComparisonOp_Gt = 4;
    public const int ComparisonOp_Le = 5;
    public const int ComparisonOp_Ge = 6;
    public const int ReductionOp_Any = 1;
    public const int ReductionOp_All = 2;

    // ── Cast ───────────────────────────────────────────────────────
    public const int Cast_Type = 1;
    public const int Cast_Input = 2;
    public const int Cast_FailureBehavior = 3;

    public const int FailureBehavior_Unspecified = 0;
    public const int FailureBehavior_ReturnNull = 1;
    public const int FailureBehavior_ThrowException = 2;

    // ── ExecutionContextVariable (oneof) ───────────────────────────
    public const int ContextVariable_CurrentTimestamp = 1;
    public const int ContextVariable_CurrentTimezone = 2;
    public const int ContextVariable_CurrentDate = 3;

    public const int VariableEvalMode_PerRecord = 2;

    // ── Type (oneof kind) ──────────────────────────────────────────
    public const int Type_Bool = 1;
    public const int Type_I8 = 2;
    public const int Type_I16 = 3;
    public const int Type_I32 = 5;
    public const int Type_I64 = 7;
    public const int Type_Fp32 = 10;
    public const int Type_Fp64 = 11;
    public const int Type_String = 12;
    public const int Type_Binary = 13;
    public const int Type_Date = 16;
    public const int Type_IntervalYear = 19;
    public const int Type_IntervalDay = 20;
    public const int Type_FixedChar = 21;
    public const int Type_VarChar = 22;
    public const int Type_FixedBinary = 23;
    public const int Type_Decimal = 24;
    public const int Type_Struct = 25;
    public const int Type_List = 27;
    public const int Type_Map = 28;
    public const int Type_UserDefined = 30;
    public const int Type_Uuid = 32;
    public const int Type_PrecisionTimestamp = 33;
    public const int Type_PrecisionTimestampTz = 34;
    public const int Type_IntervalCompound = 35;
    public const int Type_PrecisionTime = 36;
    public const int Type_Alias = 37;
    public const int Type_Func = 38;
    public const int Type_Unbound = 39;

    // Field numbers inside the simple type messages. Types with a
    // leading length/precision/scale field shift the nullability number.
    public const int SimpleType_Nullability = 2;
    public const int IntervalDayType_Precision = 3;
    public const int ParameterizedType_Length = 1;     // FixedChar, VarChar, FixedBinary
    public const int ParameterizedType_Nullability = 3;
    public const int PrecisionType_Precision = 1;      // PrecisionTime(stamp)(TZ)
    public const int DecimalType_Scale = 1;
    public const int DecimalType_Precision = 2;
    public const int DecimalType_Nullability = 4;
    public const int StructType_Types = 1;
    public const int StructType_Nullability = 3;

    public const int Nullability_Nullable = 1;
    public const int Nullability_Required = 2;

    // ── NamedStruct ────────────────────────────────────────────────
    public const int NamedStruct_Names = 1;
    public const int NamedStruct_Struct = 2;

    // ── NamedTable ─────────────────────────────────────────────────
    public const int NamedTable_Names = 1;
    public const int NamedTable_AdvancedExtension = 10;

    // ── SimpleExtensionURN / legacy SimpleExtensionURI ─────────────
    public const int ExtensionUrn_Anchor = 1;
    public const int ExtensionUrn_Urn = 2;

    // ── SimpleExtensionDeclaration (oneof mapping_type) ────────────
    public const int ExtensionDecl_ExtensionType = 1;
    public const int ExtensionDecl_ExtensionTypeVariation = 2;
    public const int ExtensionDecl_ExtensionFunction = 3;

    // ── SimpleExtensionDeclaration.ExtensionTypeVariation ──────────
    public const int ExtensionTypeVariation_Anchor = 2;

    // ── SimpleExtensionDeclaration.ExtensionFunction ───────────────
    public const int ExtensionFunction_UriReference = 1;  // legacy
    public const int ExtensionFunction_Anchor = 2;
    public const int ExtensionFunction_Name = 3;
    public const int ExtensionFunction_UrnReference = 4;
}
