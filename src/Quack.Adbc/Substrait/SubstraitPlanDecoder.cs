// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
//
// Derived from kusto_adbc (src/KustoAdbc/Substrait/SubstraitPlanDecoder.cs);
// see THIRD-PARTY-NOTICES.md. Changes: namespace; virtual tables, grouping
// sets, ordered aggregates, WITH TIES, subqueries, outer references, more
// literal types, four-part table names.

using F = Quack.Adbc.Substrait.SubstraitFields;

namespace Quack.Adbc.Substrait;

/// <summary>
/// Decodes a Substrait plan from protobuf wire format into the model in
/// SubstraitModel.cs. Fields inside relations and expressions that are not
/// understood cause the plan to be rejected, because ignoring them could
/// silently change query semantics. Plan-level metadata that cannot affect
/// results (version, expected type URLs, ...) is skipped.
/// </summary>
sealed class SubstraitPlanDecoder
{
    const string StandardUrnPrefix = "extension:io.substrait:";

    readonly byte[] _data;
    readonly Dictionary<uint, string> _urns = new();
    readonly Dictionary<uint, string> _uris = new();
    readonly Dictionary<uint, FunctionDecl> _functions = new();
    readonly HashSet<uint> _declaredTypeVariations = new();
    readonly List<(FunctionDecl Decl, uint? UrnRef, uint? UriRef)> _pendingFunctions = new();

    public SubstraitPlanDecoder(byte[] data)
    {
        _data = data;
    }

    /// <summary>True when the plan asks for execution context variables to be evaluated per record.</summary>
    public bool PerRecordEvaluation { get; private set; }

    /// <summary>Decodes the plan and returns its root relation (the first RelRoot, else the first Rel).</summary>
    public RootRel DecodePlan()
    {
        var r = new ProtoReader(_data, 0, _data.Length);
        var relations = new List<ProtoSlice>();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.Plan_ExtensionUrns: ReadUrn(r.ReadMessage(wire), _urns); break;
                case F.Plan_ExtensionUris: ReadUrn(r.ReadMessage(wire), _uris); break;
                case F.Plan_ExtensionDeclarations: ReadExtensionDeclaration(r.ReadMessage(wire)); break;
                case F.Plan_Relations: relations.Add(r.ReadSlice(wire)); break;
                case F.Plan_AdvancedExtensions: ReadPlanExtension(r.ReadMessage(wire)); break;
                case F.Plan_ExecutionBehavior: ReadExecutionBehavior(r.ReadMessage(wire)); break;
                default: r.Skip(wire); break;
            }
        }

        ResolveFunctions();

        if (relations.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("Plan contains no relations.");

        ProtoSlice? firstRel = null;
        foreach (var slice in relations)
        {
            var pr = new ProtoReader(_data, slice);
            while (pr.Next(out int field, out int wire))
            {
                if (field == F.PlanRel_Root)
                    return ReadRelRoot(pr.ReadMessage(wire));
                if (field == F.PlanRel_Rel && firstRel == null)
                    firstRel = pr.ReadSlice(wire);
                else
                    pr.Skip(wire);
            }
        }

        if (firstRel == null)
            throw SubstraitTranslationException.MalformedPlan("PlanRel has no relation.");
        return new RootRel { Input = ReadRel(new ProtoReader(_data, firstRel.Value)) };
    }

    #region Extensions

    static void ReadUrn(ProtoReader r, Dictionary<uint, string> target)
    {
        uint anchor = 0;
        string? urn = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.ExtensionUrn_Anchor: anchor = r.ReadUInt32(wire); break;
                case F.ExtensionUrn_Urn: urn = r.ReadString(wire); break;
                default: r.Skip(wire); break;
            }
        }
        if (urn != null)
            target[anchor] = NormalizeUrn(urn);
    }

    /// <summary>
    /// Maps legacy extension URIs for the standard extensions
    /// (".../extensions/functions_arithmetic.yaml") to their URNs.
    /// </summary>
    internal static string NormalizeUrn(string urnOrUri)
    {
        if (urnOrUri.StartsWith("extension:", StringComparison.Ordinal))
            return urnOrUri;
        int slash = urnOrUri.LastIndexOf('/');
        string file = slash >= 0 ? urnOrUri.Substring(slash + 1) : urnOrUri;
        // Only URIs that point into the Substrait repository, or the bare "/functions_x.yaml"
        // form older producers used, name the standard extensions.
        bool standardLocation = urnOrUri.IndexOf("substrait", StringComparison.OrdinalIgnoreCase) >= 0
            || (slash == 0 && urnOrUri.Length > 1);
        if (standardLocation && file.EndsWith(".yaml", StringComparison.Ordinal) && file.StartsWith("functions_", StringComparison.Ordinal))
            return StandardUrnPrefix + file.Substring(0, file.Length - ".yaml".Length);
        return urnOrUri;
    }

    void ReadExtensionDeclaration(ProtoReader r)
    {
        while (r.Next(out int field, out int wire))
        {
            if (field == F.ExtensionDecl_ExtensionTypeVariation)
            {
                var vr = r.ReadMessage(wire);
                while (vr.Next(out int vf, out int vw))
                {
                    if (vf == F.ExtensionTypeVariation_Anchor) _declaredTypeVariations.Add(vr.ReadUInt32(vw));
                    else vr.Skip(vw);
                }
                continue;
            }
            if (field != F.ExtensionDecl_ExtensionFunction)
            {
                // Extension types are rejected where they are used.
                r.Skip(wire);
                continue;
            }

            var fr = r.ReadMessage(wire);
            uint anchor = 0;
            uint? urnRef = null, uriRef = null;
            string? name = null;
            while (fr.Next(out int ff, out int fw))
            {
                switch (ff)
                {
                    case F.ExtensionFunction_Anchor: anchor = fr.ReadUInt32(fw); break;
                    case F.ExtensionFunction_Name: name = fr.ReadString(fw); break;
                    case F.ExtensionFunction_UrnReference: urnRef = fr.ReadUInt32(fw); break;
                    case F.ExtensionFunction_UriReference: uriRef = fr.ReadUInt32(fw); break;
                    default: fr.Skip(fw); break;
                }
            }
            if (name == null)
                throw SubstraitTranslationException.MalformedPlan($"Extension function (anchor={anchor}) has no name.");

            var decl = new FunctionDecl { RawName = name };
            int colon = name.IndexOf(':');
            if (colon >= 0)
            {
                decl.Name = name.Substring(0, colon);
                decl.Signature = name.Substring(colon + 1);
            }
            else
            {
                decl.Name = name;
            }
            _functions[anchor] = decl;
            _pendingFunctions.Add((decl, urnRef, uriRef));
        }
    }

    void ResolveFunctions()
    {
        // URN/URI declarations may follow the function declarations in the buffer,
        // so they are resolved once the whole plan header has been read.
        foreach (var (decl, urnRef, uriRef) in _pendingFunctions)
        {
            if (urnRef.HasValue && _urns.TryGetValue(urnRef.Value, out var urn))
                decl.Urn = urn;
            else if (uriRef.HasValue && _uris.TryGetValue(uriRef.Value, out var uri))
                decl.Urn = uri;
        }
    }

    FunctionDecl ResolveFunction(uint anchor)
    {
        if (!_functions.TryGetValue(anchor, out var decl))
            throw SubstraitTranslationException.UndeclaredFunction((int)anchor);
        return decl;
    }

    void ReadExecutionBehavior(ProtoReader r)
    {
        while (r.Next(out int field, out int wire))
        {
            if (field == F.ExecutionBehavior_VariableEvalMode)
                PerRecordEvaluation = r.ReadInt32(wire) == F.VariableEvalMode_PerRecord;
            else
                r.Skip(wire);
        }
    }

    /// <summary>
    /// Optimizations may be ignored, but an enhancement changes the semantics of
    /// the message it is attached to, so it cannot be.
    /// </summary>
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] The type URLs of the plan's optimization hints.</summary>
    public HashSet<string> PlanOptimizations { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] The plan's advanced extension: optimization hints are recorded (a consumer may
    /// act on one it knows); an enhancement changes the plan's meaning and is refused.
    /// </summary>
    void ReadPlanExtension(ProtoReader r)
    {
        while (r.Next(out int field, out int wire))
        {
            if (field == F.AdvancedExtension_Enhancement)
                throw SubstraitTranslationException.Unsupported("Plan carries an advanced extension enhancement, which changes its semantics.");
            if (field != F.AdvancedExtension_Optimization)
            {
                r.Skip(wire);
                continue;
            }
            var any = r.ReadMessage(wire);
            while (any.Next(out int af, out int aw))
            {
                if (af == F.Any_TypeUrl) PlanOptimizations.Add(any.ReadString(aw));
                else any.Skip(aw);
            }
        }
    }

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] A CrossRel's advanced extension: <c>fabricator.apply</c> makes it an APPLY
    /// (true); any other enhancement changes its meaning in a way this consumer does not know.
    /// </summary>
    static bool ReadCrossEnhancement(ProtoReader r)
    {
        bool apply = false;
        while (r.Next(out int field, out int wire))
        {
            if (field != F.AdvancedExtension_Enhancement)
            {
                r.Skip(wire);
                continue;
            }
            var any = r.ReadMessage(wire);
            string? typeUrl = null;
            while (any.Next(out int af, out int aw))
            {
                if (af == F.Any_TypeUrl) typeUrl = any.ReadString(aw);
                else any.Skip(aw);
            }
            if (typeUrl != F.Enhancement_FabricatorApply)
                throw SubstraitTranslationException.Unsupported($"CrossRel carries the enhancement '{typeUrl}', which changes its semantics.");
            apply = true;
        }
        return apply;
    }

    static void CheckAdvancedExtension(ProtoReader r, string owner)
    {
        while (r.Next(out int field, out int wire))
        {
            if (field == F.AdvancedExtension_Enhancement)
                throw SubstraitTranslationException.Unsupported($"{owner} carries an advanced extension enhancement, which changes its semantics.");
            r.Skip(wire);
        }
    }

    #endregion

    #region Relations

    RootRel ReadRelRoot(ProtoReader r)
    {
        Rel? input = null;
        var names = new List<string>();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.RelRoot_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.RelRoot_Names: names.Add(r.ReadString(wire)); break;
                default: throw SubstraitTranslationException.UnexpectedField("RelRoot", field);
            }
        }
        if (input == null)
            throw SubstraitTranslationException.MalformedPlan("RelRoot has no input.");
        return new RootRel { Input = input, Names = names };
    }

    Rel ReadRel(ProtoReader r)
    {
        Rel? rel = null;
        while (r.Next(out int field, out int wire))
        {
            if (rel != null)
                throw SubstraitTranslationException.MalformedPlan("Rel contains more than one relation.");
            switch (field)
            {
                case F.Rel_Read: rel = ReadReadRel(r.ReadMessage(wire)); break;
                case F.Rel_Filter: rel = ReadFilterRel(r.ReadMessage(wire)); break;
                case F.Rel_Fetch: rel = ReadFetchRel(r.ReadMessage(wire)); break;
                case F.Rel_Aggregate: rel = ReadAggregateRel(r.ReadMessage(wire)); break;
                case F.Rel_Sort: rel = ReadSortRel(r.ReadMessage(wire)); break;
                case F.Rel_Join: rel = ReadJoinRel(r.ReadMessage(wire)); break;
                case F.Rel_Project: rel = ReadProjectRel(r.ReadMessage(wire)); break;
                case F.Rel_Set: rel = ReadSetRel(r.ReadMessage(wire)); break;
                case F.Rel_Cross: rel = ReadCrossRel(r.ReadMessage(wire)); break;
                case F.Rel_TopN: rel = ReadTopNRel(r.ReadMessage(wire)); break;
                case F.Rel_Window: rel = ReadWindowRel(r.ReadMessage(wire)); break; // [FABRICATOR-PATCH: OURS-BY-DESIGN] window functions
                case F.Rel_Write: rel = ReadWriteRel(r.ReadMessage(wire)); break; // [FABRICATOR-PATCH: OURS-BY-DESIGN] INSERT … SELECT
                default: throw SubstraitTranslationException.UnsupportedRelation(field);
            }
        }
        return rel ?? throw SubstraitTranslationException.MalformedPlan("Rel is empty.");
    }

    List<int>? ReadRelCommon(ProtoReader r, string owner)
    {
        List<int>? emit = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.RelCommon_Direct:
                    r.Skip(wire);
                    break;
                case F.RelCommon_Emit:
                {
                    emit = new List<int>();
                    var er = r.ReadMessage(wire);
                    while (er.Next(out int ef, out int ew))
                    {
                        if (ef == F.Emit_OutputMapping) er.ReadRepeatedInt32(ew, emit);
                        else throw SubstraitTranslationException.UnexpectedField("RelCommon.Emit", ef);
                    }
                    break;
                }
                case F.RelCommon_Hint:
                case F.RelCommon_RelAnchor:
                    // Hints and anchors don't affect results.
                    r.Skip(wire);
                    break;
                case F.RelCommon_AdvancedExtension:
                    CheckAdvancedExtension(r.ReadMessage(wire), owner);
                    break;
                default:
                    throw SubstraitTranslationException.UnexpectedField("RelCommon", field);
            }
        }
        return emit;
    }

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] A WriteRel: the target named table, the columns written
    /// (<c>table_schema</c>, matched by the input's columns in order), the operation and the output mode.
    /// </summary>
    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] A WriteRel's advanced extension: the one enhancement understood is
    /// <c>fabricator.merge</c> (the host's MERGE, see TSqlEmitter.EmitMerge); any other changes the rel's meaning in
    /// a way this consumer does not know, so the plan is refused.
    /// </summary>
    static void ReadWriteEnhancement(ProtoReader r, WriteRel rel)
    {
        while (r.Next(out int field, out int wire))
        {
            if (field != F.AdvancedExtension_Enhancement)
            {
                r.Skip(wire);
                continue;
            }
            var any = r.ReadMessage(wire);
            string? typeUrl = null;
            string value = "";
            while (any.Next(out int af, out int aw))
            {
                if (af == F.Any_TypeUrl) typeUrl = any.ReadString(aw);
                else if (af == F.Any_Value) value = System.Text.Encoding.UTF8.GetString(any.ReadBytes(aw));
                else any.Skip(aw);
            }
            if (typeUrl != F.Enhancement_FabricatorMerge)
                throw SubstraitTranslationException.Unsupported($"WriteRel carries the enhancement '{typeUrl}', which changes its semantics.");
            rel.Enhancement = typeUrl;
            rel.EnhancementValue = value;
        }
    }

    WriteRel ReadWriteRel(ProtoReader r)
    {
        var rel = new WriteRel();
        var schema = new ReadRel();
        bool hasSchema = false;
        Rel? input = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.WriteRel_NamedTable:
                {
                    var nr = r.ReadMessage(wire);
                    while (nr.Next(out int nf, out int nw))
                    {
                        if (nf == F.NamedTable_Names) rel.TableNames.Add(nr.ReadString(nw));
                        else if (nf == F.NamedTable_AdvancedExtension) CheckAdvancedExtension(nr.ReadMessage(nw), "NamedObjectWrite");
                        else throw SubstraitTranslationException.UnexpectedField("NamedObjectWrite", nf);
                    }
                    break;
                }
                case F.WriteRel_ExtensionTable:
                    throw SubstraitTranslationException.Unsupported("WriteRel extension_table is not supported; only named tables can be written.");
                case F.WriteRel_TableSchema: ReadNamedStruct(r.ReadMessage(wire), schema); hasSchema = true; break;
                case F.WriteRel_Op: rel.Op = r.ReadInt32(wire); break;
                case F.WriteRel_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.WriteRel_Output: rel.Output = r.ReadInt32(wire); break;
                case F.WriteRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "WriteRel"); break;
                case F.WriteRel_CreateMode: rel.CreateMode = r.ReadInt32(wire); break;
                case F.WriteRel_AdvancedExtension: ReadWriteEnhancement(r.ReadMessage(wire), rel); break;
                default: throw SubstraitTranslationException.UnexpectedField("WriteRel", field);
            }
        }
        if (rel.TableNames.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("WriteRel has no named_table.");
        if (!hasSchema)
            throw SubstraitTranslationException.MalformedPlan("WriteRel has no table_schema.");
        rel.ColumnNames = schema.ColumnNames;
        rel.ColumnTypes = schema.ColumnTypes;
        rel.Input = input ?? throw SubstraitTranslationException.MalformedPlan("WriteRel missing input.");
        return rel;
    }

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] A ReadRel's advanced extension: <c>fabricator.table_function</c> makes it a
    /// call of the named table function, its value a struct of the argument expressions (returned); any other
    /// enhancement changes its meaning in a way this consumer does not know. Null without an enhancement.
    /// </summary>
    List<Expr>? ReadReadEnhancement(ProtoReader r)
    {
        List<Expr>? args = null;
        while (r.Next(out int field, out int wire))
        {
            if (field != F.AdvancedExtension_Enhancement)
            {
                r.Skip(wire);
                continue;
            }
            var any = r.ReadMessage(wire);
            string? typeUrl = null;
            byte[] value = Array.Empty<byte>();
            while (any.Next(out int af, out int aw))
            {
                if (af == F.Any_TypeUrl) typeUrl = any.ReadString(aw);
                else if (af == F.Any_Value) value = any.ReadBytes(aw);
                else any.Skip(aw);
            }
            if (typeUrl != F.Enhancement_FabricatorTableFunction)
                throw SubstraitTranslationException.Unsupported($"ReadRel carries the enhancement '{typeUrl}', which changes its semantics.");
            args = new List<Expr>();
            var fields = new ProtoReader(value, 0, value.Length);
            while (fields.Next(out int sf, out int sw))
            {
                if (sf == 1) args.Add(ReadExpression(fields.ReadMessage(sw))); // Expression.Nested.Struct.fields
                else fields.Skip(sw);
            }
        }
        return args;
    }

    ReadRel ReadReadRel(ProtoReader r)
    {
        var rel = new ReadRel();
        bool hasSchema = false, hasTable = false;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.ReadRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "ReadRel"); break;
                case F.ReadRel_BaseSchema: ReadNamedStruct(r.ReadMessage(wire), rel); hasSchema = true; break;
                case F.ReadRel_Filter: rel.Filter = ReadExpression(r.ReadMessage(wire)); break;
                case F.ReadRel_BestEffortFilter: rel.BestEffortFilter = ReadExpression(r.ReadMessage(wire)); break;
                case F.ReadRel_Projection: rel.Projection = ReadMaskExpression(r.ReadMessage(wire)); break;
                case F.ReadRel_NamedTable: ReadNamedTable(r.ReadMessage(wire), rel); hasTable = true; break;
                case F.Rel_AdvancedExtension: rel.TableFunctionArgs ??= ReadReadEnhancement(r.ReadMessage(wire)); break;
                case F.ReadRel_VirtualTable: rel.VirtualRows = ReadVirtualTable(r.ReadMessage(wire)); hasTable = true; break;
                case F.ReadRel_LocalFiles:
                case F.ReadRel_ExtensionTable:
                case F.ReadRel_IcebergTable:
                    throw SubstraitTranslationException.Unsupported($"ReadRel read type (field {field}) is not supported; only named tables can be read.");
                default:
                    throw SubstraitTranslationException.UnexpectedField("ReadRel", field);
            }
        }
        if (!hasTable)
            throw SubstraitTranslationException.MalformedPlan("ReadRel has no named_table or virtual_table.");
        if (!hasSchema)
            throw SubstraitTranslationException.MalformedPlan("ReadRel has no base_schema.");
        if (rel.TableFunctionArgs != null)
        {
            if (rel.TableNames.Count == 0 || rel.VirtualRows != null)
                throw SubstraitTranslationException.MalformedPlan("A table-function ReadRel must name the function.");
            return rel;
        }
        if (rel.VirtualRows != null && rel.VirtualRows.Any(row => row.Count != rel.ColumnNames.Count))
            throw SubstraitTranslationException.MalformedPlan("A virtual table row does not have one value per base_schema column.");
        return rel;
    }

    List<List<Expr>> ReadVirtualTable(ProtoReader r)
    {
        var rows = new List<List<Expr>>();
        while (r.Next(out int field, out int wire))
        {
            var row = new List<Expr>();
            var sr = r.ReadMessage(wire);
            switch (field)
            {
                case F.VirtualTable_Expressions:
                    while (sr.Next(out int sf, out int sw))
                    {
                        if (sf == F.NestedStruct_Fields) row.Add(ReadExpression(sr.ReadMessage(sw)));
                        else throw SubstraitTranslationException.UnexpectedField("Expression.Nested.Struct", sf);
                    }
                    break;
                case F.VirtualTable_Values:
                    while (sr.Next(out int sf, out int sw))
                    {
                        if (sf == F.LiteralStruct_Fields) row.Add(ReadLiteral(sr.ReadMessage(sw)));
                        else throw SubstraitTranslationException.UnexpectedField("Literal.Struct", sf);
                    }
                    break;
                default:
                    throw SubstraitTranslationException.UnexpectedField("ReadRel.VirtualTable", field);
            }
            rows.Add(row);
        }
        return rows;
    }

    void ReadNamedStruct(ProtoReader r, ReadRel rel)
    {
        bool hasStruct = false;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.NamedStruct_Names: rel.ColumnNames.Add(r.ReadString(wire)); break;
                case F.NamedStruct_Struct:
                {
                    hasStruct = true;
                    var sr = r.ReadMessage(wire);
                    while (sr.Next(out int sf, out int sw))
                    {
                        if (sf == F.StructType_Types) rel.ColumnTypes.Add(ReadType(sr.ReadMessage(sw)));
                        else sr.Skip(sw);
                    }
                    break;
                }
                default: throw SubstraitTranslationException.UnexpectedField("NamedStruct", field);
            }
        }

        if (!hasStruct)
        {
            foreach (var _ in rel.ColumnNames)
                rel.ColumnTypes.Add(SType.Unknown);
        }
        else if (rel.ColumnTypes.Count != rel.ColumnNames.Count)
        {
            // Names include the fields of nested structs.
            throw SubstraitTranslationException.Unsupported("base_schema contains nested struct columns, which SQL Server tables cannot have.");
        }
        foreach (var t in rel.ColumnTypes)
        {
            if (t.Kind is TypeKind.Struct or TypeKind.List or TypeKind.Map)
                throw SubstraitTranslationException.Unsupported($"base_schema column type '{t}' is not supported.");
        }
    }

    static void ReadNamedTable(ProtoReader r, ReadRel rel)
    {
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.NamedTable_Names: rel.TableNames.Add(r.ReadString(wire)); break;
                case F.NamedTable_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "NamedTable"); break;
                default: throw SubstraitTranslationException.UnexpectedField("NamedTable", field);
            }
        }
        if (rel.TableNames.Count is 0 or > 4)
            throw SubstraitTranslationException.Unsupported($"NamedTable must have 1 to 4 name parts ([[[server,] database,] schema,] table); found {rel.TableNames.Count}.");
    }

    static List<int> ReadMaskExpression(ProtoReader r)
    {
        var fields = new List<int>();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.MaskExpression_Select:
                {
                    var sr = r.ReadMessage(wire);
                    while (sr.Next(out int sf, out int sw))
                    {
                        if (sf != F.StructSelect_StructItems)
                            throw SubstraitTranslationException.UnexpectedField("MaskExpression.StructSelect", sf);
                        var ir = sr.ReadMessage(sw);
                        int index = 0;
                        while (ir.Next(out int itf, out int itw))
                        {
                            if (itf == F.StructItem_Field) index = ir.ReadInt32(itw);
                            else if (itf == F.StructItem_Child)
                                throw SubstraitTranslationException.Unsupported("Projection into nested struct fields is not supported.");
                            else throw SubstraitTranslationException.UnexpectedField("MaskExpression.StructItem", itf);
                        }
                        fields.Add(index);
                    }
                    break;
                }
                case F.MaskExpression_MaintainSingularStruct:
                    r.Skip(wire);
                    break;
                default:
                    throw SubstraitTranslationException.UnexpectedField("MaskExpression", field);
            }
        }
        return fields;
    }

    FilterRel ReadFilterRel(ProtoReader r)
    {
        var rel = new FilterRel();
        Rel? input = null;
        Expr? condition = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.FilterRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "FilterRel"); break;
                case F.FilterRel_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.FilterRel_Condition: condition = ReadExpression(r.ReadMessage(wire)); break;
                case F.Rel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "FilterRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("FilterRel", field);
            }
        }
        rel.Input = input ?? throw SubstraitTranslationException.MalformedPlan("FilterRel missing input.");
        rel.Condition = condition ?? throw SubstraitTranslationException.MalformedPlan("FilterRel missing condition.");
        return rel;
    }

    ProjectRel ReadProjectRel(ProtoReader r)
    {
        var rel = new ProjectRel();
        Rel? input = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.ProjectRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "ProjectRel"); break;
                case F.ProjectRel_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.ProjectRel_Expressions: rel.Expressions.Add(ReadExpression(r.ReadMessage(wire))); break;
                case F.Rel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "ProjectRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("ProjectRel", field);
            }
        }
        rel.Input = input ?? throw SubstraitTranslationException.MalformedPlan("ProjectRel missing input.");
        return rel;
    }

    FetchRel ReadFetchRel(ProtoReader r)
    {
        var rel = new FetchRel();
        Rel? input = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.FetchRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "FetchRel"); break;
                case F.FetchRel_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.FetchRel_Offset: rel.Offset = Int64Literal(r.ReadInt64(wire)); break;
                case F.FetchRel_Count:
                {
                    // In the legacy encoding a count of -1 meant "all rows".
                    long count = r.ReadInt64(wire);
                    rel.Count = count < 0 ? null : Int64Literal(count);
                    break;
                }
                case F.FetchRel_OffsetExpr: rel.Offset = ReadExpression(r.ReadMessage(wire)); break;
                case F.FetchRel_CountExpr: rel.Count = ReadExpression(r.ReadMessage(wire)); break;
                case F.Rel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "FetchRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("FetchRel", field);
            }
        }
        rel.Input = input ?? throw SubstraitTranslationException.MalformedPlan("FetchRel missing input.");
        return rel;
    }

    static LiteralExpr Int64Literal(long value) => new() { Type = SType.I64, IntValue = value };

    SortRel ReadSortRel(ProtoReader r)
    {
        var rel = new SortRel();
        Rel? input = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.SortRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "SortRel"); break;
                case F.SortRel_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.SortRel_Sorts: rel.Sorts.Add(ReadSortField(r.ReadMessage(wire))); break;
                case F.Rel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "SortRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("SortRel", field);
            }
        }
        rel.Input = input ?? throw SubstraitTranslationException.MalformedPlan("SortRel missing input.");
        return rel;
    }

    SortField ReadSortField(ProtoReader r)
    {
        var sf = new SortField();
        Expr? expr = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.SortField_Expr: expr = ReadExpression(r.ReadMessage(wire)); break;
                case F.SortField_Direction: sf.Direction = r.ReadInt32(wire); break;
                case F.SortField_ComparisonFunctionReference:
                    throw SubstraitTranslationException.Unsupported("Sorting with a custom comparison function is not supported.");
                default: throw SubstraitTranslationException.UnexpectedField("SortField", field);
            }
        }
        sf.Expr = expr ?? throw SubstraitTranslationException.MalformedPlan("SortField missing expression.");
        return sf;
    }

    // [FABRICATOR-PATCH: OURS-BY-DESIGN] ConsistentPartitionWindowRel
    WindowRel ReadWindowRel(ProtoReader r)
    {
        var rel = new WindowRel();
        Rel? input = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.WindowRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "ConsistentPartitionWindowRel"); break;
                case F.WindowRel_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.WindowRel_WindowFunctions: rel.Functions.Add(ReadWindowCall(r.ReadMessage(wire))); break;
                case F.WindowRel_PartitionExpressions: rel.Partitions.Add(ReadExpression(r.ReadMessage(wire))); break;
                case F.WindowRel_Sorts: rel.Sorts.Add(ReadSortField(r.ReadMessage(wire))); break;
                case F.Rel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "ConsistentPartitionWindowRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("ConsistentPartitionWindowRel", field);
            }
        }
        rel.Input = input ?? throw SubstraitTranslationException.MalformedPlan("ConsistentPartitionWindowRel missing input.");
        if (rel.Functions.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("ConsistentPartitionWindowRel has no window functions.");
        return rel;
    }

    WindowCall ReadWindowCall(ProtoReader r)
    {
        var call = new WindowCall();
        uint functionRef = 0;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.WindowRelFunction_FunctionReference: functionRef = r.ReadUInt32(wire); break;
                case F.WindowRelFunction_Arguments: call.Args.Add(ReadFunctionArgument(r.ReadMessage(wire))); break;
                case F.WindowRelFunction_Options: call.Options.Add(ReadFunctionOption(r.ReadMessage(wire))); break;
                case F.WindowRelFunction_OutputType: call.OutputType = ReadType(r.ReadMessage(wire)); break;
                case F.WindowRelFunction_Invocation: call.Invocation = r.ReadInt32(wire); break;
                case F.WindowRelFunction_BoundsType: call.BoundsType = r.ReadInt32(wire); break;
                case F.WindowRelFunction_LowerBound: call.Lower = ReadWindowBound(r.ReadMessage(wire)); break;
                case F.WindowRelFunction_UpperBound: call.Upper = ReadWindowBound(r.ReadMessage(wire)); break;
                case F.WindowRelFunction_Phase:
                {
                    int phase = r.ReadInt32(wire);
                    if (phase is not (F.AggregationPhase_Unspecified or F.AggregationPhase_InitialToResult))
                        throw SubstraitTranslationException.Unsupported($"Aggregation phase {phase} is not supported; only INITIAL_TO_RESULT can be translated.");
                    break;
                }
                default: throw SubstraitTranslationException.UnexpectedField("WindowRelFunction", field);
            }
        }
        call.Function = ResolveFunction(functionRef);
        return call;
    }

    WindowBound ReadWindowBound(ProtoReader r)
    {
        var bound = new WindowBound();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.WindowBound_Preceding:
                case F.WindowBound_Following:
                {
                    bound.Kind = field;
                    var offset = r.ReadMessage(wire);
                    while (offset.Next(out int of, out int ow))
                    {
                        switch (of)
                        {
                            case F.WindowBoundOffset_Offset: bound.Offset ??= Int64Literal(offset.ReadInt64(ow)); break;
                            case F.WindowBoundOffset_OffsetExpr: bound.Offset = ReadExpression(offset.ReadMessage(ow)); break;
                            default: throw SubstraitTranslationException.UnexpectedField("WindowFunction.Bound offset", of);
                        }
                    }
                    break;
                }
                case F.WindowBound_CurrentRow:
                case F.WindowBound_Unbounded:
                    bound.Kind = field;
                    r.ReadMessage(wire);
                    break;
                default: throw SubstraitTranslationException.UnexpectedField("WindowFunction.Bound", field);
            }
        }
        if (bound.Kind == 0)
            throw SubstraitTranslationException.MalformedPlan("A window bound has no kind.");
        return bound;
    }

    AggregateRel ReadAggregateRel(ProtoReader r)
    {
        var rel = new AggregateRel();
        Rel? input = null;
        var groupings = new List<(List<int>? Refs, List<Expr>? Inline)>();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.AggregateRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "AggregateRel"); break;
                case F.AggregateRel_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.AggregateRel_Groupings: groupings.Add(ReadGrouping(r.ReadMessage(wire))); break;
                case F.AggregateRel_Measures: rel.Measures.Add(ReadMeasure(r.ReadMessage(wire))); break;
                case F.AggregateRel_GroupingExpressions: rel.GroupingExpressions.Add(ReadExpression(r.ReadMessage(wire))); break;
                case F.Rel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "AggregateRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("AggregateRel", field);
            }
        }
        rel.Input = input ?? throw SubstraitTranslationException.MalformedPlan("AggregateRel missing input.");

        // Producers may write both the current expression references and, for older consumers,
        // the same expressions inline; the references take precedence. A plan with only inline
        // expressions (the legacy encoding) gets them appended to the grouping expressions.
        bool useReferences = rel.GroupingExpressions.Count > 0 || groupings.All(g => g.Inline == null);
        foreach (var (refs, inline) in groupings)
        {
            var set = new List<int>();
            if (useReferences)
            {
                foreach (int index in refs ?? new List<int>())
                {
                    if (index < 0 || index >= rel.GroupingExpressions.Count)
                        throw SubstraitTranslationException.MalformedPlan($"Grouping set references grouping expression {index}, but there are {rel.GroupingExpressions.Count}.");
                    set.Add(index);
                }
            }
            else
            {
                foreach (var e in inline ?? new List<Expr>())
                {
                    set.Add(rel.GroupingExpressions.Count);
                    rel.GroupingExpressions.Add(e);
                }
            }
            rel.GroupingSets.Add(set);
        }

        for (int i = 0; i < rel.GroupingExpressions.Count; i++)
        {
            if (!rel.GroupingSets.Any(set => set.Contains(i)))
                throw SubstraitTranslationException.MalformedPlan($"Grouping expression {i} is not referenced by any grouping set.");
        }
        if (rel.GroupingSets.Count == 0 && rel.Measures.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("AggregateRel has neither grouping sets nor measures.");
        if (rel.Measures.Count == 0 && rel.GroupingExpressions.Count == 0 && rel.GroupingSets.Count == 1)
            throw SubstraitTranslationException.MalformedPlan("An aggregate with only the empty grouping set and no measures has no columns.");
        return rel;
    }

    (List<int>? Refs, List<Expr>? Inline) ReadGrouping(ProtoReader r)
    {
        List<int>? refs = null;
        List<Expr>? inline = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.Grouping_ExpressionReferences:
                    refs ??= new List<int>();
                    r.ReadRepeatedInt32(wire, refs);
                    break;
                case F.Grouping_GroupingExpressions:
                    (inline ??= new List<Expr>()).Add(ReadExpression(r.ReadMessage(wire)));
                    break;
                default:
                    throw SubstraitTranslationException.UnexpectedField("AggregateRel.Grouping", field);
            }
        }
        return (refs, inline);
    }

    Measure ReadMeasure(ProtoReader r)
    {
        var m = new Measure();
        AggregateCall? call = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.Measure_Measure: call = ReadAggregateFunction(r.ReadMessage(wire)); break;
                case F.Measure_Filter: m.Filter = ReadExpression(r.ReadMessage(wire)); break;
                default: throw SubstraitTranslationException.UnexpectedField("AggregateRel.Measure", field);
            }
        }
        m.Call = call ?? throw SubstraitTranslationException.MalformedPlan("Measure has no aggregate function.");
        return m;
    }

    AggregateCall ReadAggregateFunction(ProtoReader r)
    {
        var call = new AggregateCall();
        uint functionRef = 0;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.AggregateFunction_FunctionReference: functionRef = r.ReadUInt32(wire); break;
                case F.AggregateFunction_Arguments: call.Args.Add(ReadFunctionArgument(r.ReadMessage(wire))); break;
                case F.AggregateFunction_Args: call.Args.Add(new FunctionArg { Value = ReadExpression(r.ReadMessage(wire)) }); break;
                case F.AggregateFunction_Options: call.Options.Add(ReadFunctionOption(r.ReadMessage(wire))); break;
                case F.AggregateFunction_OutputType: call.OutputType = ReadType(r.ReadMessage(wire)); break;
                case F.AggregateFunction_Invocation: call.Invocation = r.ReadInt32(wire); break;
                case F.AggregateFunction_Phase:
                {
                    int phase = r.ReadInt32(wire);
                    if (phase is not (F.AggregationPhase_Unspecified or F.AggregationPhase_InitialToResult))
                        throw SubstraitTranslationException.Unsupported($"Aggregation phase {phase} is not supported; only INITIAL_TO_RESULT can be translated.");
                    break;
                }
                case F.AggregateFunction_Sorts: call.Sorts.Add(ReadSortField(r.ReadMessage(wire))); break;
                default:
                    throw SubstraitTranslationException.UnexpectedField("AggregateFunction", field);
            }
        }
        call.Function = ResolveFunction(functionRef);
        return call;
    }

    JoinRel ReadJoinRel(ProtoReader r)
    {
        var rel = new JoinRel();
        Rel? left = null, right = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.JoinRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "JoinRel"); break;
                case F.JoinRel_Left: left = ReadRel(r.ReadMessage(wire)); break;
                case F.JoinRel_Right: right = ReadRel(r.ReadMessage(wire)); break;
                case F.JoinRel_Expression: rel.Expression = ReadExpression(r.ReadMessage(wire)); break;
                case F.JoinRel_PostJoinFilter: rel.PostJoinFilter = ReadExpression(r.ReadMessage(wire)); break;
                case F.JoinRel_Type: rel.Type = r.ReadInt32(wire); break;
                case F.Rel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "JoinRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("JoinRel", field);
            }
        }
        if (left == null || right == null)
            throw SubstraitTranslationException.MalformedPlan("JoinRel missing left or right input.");
        rel.Left = left;
        rel.Right = right;
        return rel;
    }

    CrossRel ReadCrossRel(ProtoReader r)
    {
        var rel = new CrossRel();
        Rel? left = null, right = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.CrossRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "CrossRel"); break;
                case F.CrossRel_Left: left = ReadRel(r.ReadMessage(wire)); break;
                case F.CrossRel_Right: right = ReadRel(r.ReadMessage(wire)); break;
                case F.Rel_AdvancedExtension: rel.Apply = ReadCrossEnhancement(r.ReadMessage(wire)); break;
                default: throw SubstraitTranslationException.UnexpectedField("CrossRel", field);
            }
        }
        if (left == null || right == null)
            throw SubstraitTranslationException.MalformedPlan("CrossRel missing left or right input.");
        rel.Left = left;
        rel.Right = right;
        return rel;
    }

    SetRel ReadSetRel(ProtoReader r)
    {
        var rel = new SetRel();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.SetRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "SetRel"); break;
                case F.SetRel_Inputs: rel.Inputs.Add(ReadRel(r.ReadMessage(wire))); break;
                case F.SetRel_Op: rel.Op = r.ReadInt32(wire); break;
                case F.Rel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "SetRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("SetRel", field);
            }
        }
        if (rel.Inputs.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("SetRel has no inputs.");
        return rel;
    }

    TopNRel ReadTopNRel(ProtoReader r)
    {
        var rel = new TopNRel();
        Rel? input = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.TopNRel_Common: rel.Emit = ReadRelCommon(r.ReadMessage(wire), "TopNRel"); break;
                case F.TopNRel_Input: input = ReadRel(r.ReadMessage(wire)); break;
                case F.TopNRel_Sorts: rel.Sorts.Add(ReadSortField(r.ReadMessage(wire))); break;
                case F.TopNRel_Offset: rel.Offset = ReadExpression(r.ReadMessage(wire)); break;
                case F.TopNRel_Count: rel.Count = ReadExpression(r.ReadMessage(wire)); break;
                case F.TopNRel_Mode:
                {
                    int mode = r.ReadInt32(wire);
                    if (mode is not (F.FetchMode_Unspecified or F.FetchMode_RowsOnly or F.FetchMode_WithTies))
                        throw SubstraitTranslationException.MalformedPlan($"TopNRel mode {mode} is not valid.");
                    rel.WithTies = mode == F.FetchMode_WithTies;
                    break;
                }
                case F.TopNRel_AdvancedExtension: CheckAdvancedExtension(r.ReadMessage(wire), "TopNRel"); break;
                default: throw SubstraitTranslationException.UnexpectedField("TopNRel", field);
            }
        }
        rel.Input = input ?? throw SubstraitTranslationException.MalformedPlan("TopNRel missing input.");
        return rel;
    }

    #endregion

    #region Expressions

    Expr ReadExpression(ProtoReader r)
    {
        Expr? expr = null;
        while (r.Next(out int field, out int wire))
        {
            if (expr != null)
                throw SubstraitTranslationException.MalformedPlan("Expression contains more than one variant.");
            switch (field)
            {
                case F.Expression_Literal: expr = ReadLiteral(r.ReadMessage(wire)); break;
                case F.Expression_Selection: expr = ReadFieldReference(r.ReadMessage(wire)); break;
                case F.Expression_ScalarFunction: expr = ReadScalarFunction(r.ReadMessage(wire)); break;
                case F.Expression_IfThen: expr = ReadIfThen(r.ReadMessage(wire)); break;
                case F.Expression_SwitchExpression: expr = ReadSwitch(r.ReadMessage(wire)); break;
                case F.Expression_SingularOrList: expr = ReadSingularOrList(r.ReadMessage(wire)); break;
                case F.Expression_MultiOrList: expr = ReadMultiOrList(r.ReadMessage(wire)); break;
                case F.Expression_Cast: expr = ReadCast(r.ReadMessage(wire)); break;
                case F.Expression_ExecutionContextVariable: expr = ReadContextVariable(r.ReadMessage(wire)); break;
                case F.Expression_WindowFunction:
                    throw SubstraitTranslationException.UnsupportedExpression("window functions are not supported.");
                case F.Expression_Subquery: expr = ReadSubquery(r.ReadMessage(wire)); break;
                case F.Expression_Nested:
                    throw SubstraitTranslationException.UnsupportedExpression("nested (struct/list/map) constructors are not supported.");
                case F.Expression_DynamicParameter:
                    throw SubstraitTranslationException.UnsupportedExpression("dynamic parameters are not supported.");
                case F.Expression_Lambda:
                case F.Expression_LambdaInvocation:
                case F.Expression_NamedLambdaReference:
                    throw SubstraitTranslationException.UnsupportedExpression("lambdas are not supported.");
                default:
                    throw SubstraitTranslationException.UnexpectedField("Expression", field);
            }
        }
        return expr ?? throw SubstraitTranslationException.UnsupportedExpression("Expression is empty.");
    }

    LiteralExpr ReadLiteral(ProtoReader r)
    {
        var lit = new LiteralExpr();
        bool found = false;
        bool nullable = false;
        while (r.Next(out int field, out int wire))
        {
            if (field is F.Literal_Nullable)
            {
                nullable = r.ReadBool(wire);
                continue;
            }
            if (field is F.Literal_TypeVariationReference)
            {
                RejectTypeVariation(r.ReadUInt32(wire));
                continue;
            }
            if (found)
                throw SubstraitTranslationException.MalformedPlan("Literal contains more than one value.");
            found = true;

            switch (field)
            {
                case F.Literal_Boolean: lit.Type = new SType(TypeKind.Bool); lit.BoolValue = r.ReadBool(wire); break;
                case F.Literal_I8: lit.Type = new SType(TypeKind.I8); lit.IntValue = r.ReadInt32(wire); break;
                case F.Literal_I16: lit.Type = new SType(TypeKind.I16); lit.IntValue = r.ReadInt32(wire); break;
                case F.Literal_I32: lit.Type = new SType(TypeKind.I32); lit.IntValue = r.ReadInt32(wire); break;
                case F.Literal_I64: lit.Type = new SType(TypeKind.I64); lit.IntValue = r.ReadInt64(wire); break;
                case F.Literal_Fp32: lit.Type = new SType(TypeKind.Fp32); lit.FloatValue = r.ReadFloat(wire); break;
                case F.Literal_Fp64: lit.Type = new SType(TypeKind.Fp64); lit.FloatValue = r.ReadDouble(wire); break;
                case F.Literal_String: lit.Type = new SType(TypeKind.String); lit.StringValue = r.ReadString(wire); break;
                case F.Literal_FixedChar:
                    lit.StringValue = r.ReadString(wire);
                    lit.Type = new SType(TypeKind.FixedChar, precision: lit.StringValue.Length);
                    break;
                case F.Literal_VarChar:
                {
                    lit.Type = new SType(TypeKind.VarChar);
                    var vr = r.ReadMessage(wire);
                    while (vr.Next(out int vf, out int vw))
                    {
                        if (vf == F.VarCharLiteral_Value) lit.StringValue = vr.ReadString(vw);
                        else vr.Skip(vw);
                    }
                    lit.StringValue ??= "";
                    break;
                }
                case F.Literal_Date: lit.Type = new SType(TypeKind.Date); lit.IntValue = r.ReadInt32(wire); break;
                case F.Literal_Binary: lit.Type = new SType(TypeKind.Binary); lit.BytesValue = r.ReadBytes(wire); break;
                case F.Literal_FixedBinary:
                    lit.BytesValue = r.ReadBytes(wire);
                    lit.Type = new SType(TypeKind.FixedBinary, precision: lit.BytesValue.Length);
                    break;
                case F.Literal_PrecisionTime:
                {
                    var tr = r.ReadMessage(wire);
                    int precision = 0;
                    while (tr.Next(out int tf, out int tw))
                    {
                        if (tf == F.PrecisionTimeLiteral_Precision) precision = tr.ReadInt32(tw);
                        else if (tf == F.PrecisionTimeLiteral_Value) lit.IntValue = tr.ReadInt64(tw);
                        else tr.Skip(tw);
                    }
                    lit.Type = new SType(TypeKind.PrecisionTime, precision: precision);
                    break;
                }
                case F.Literal_PrecisionTimestamp:
                case F.Literal_PrecisionTimestampTz:
                {
                    var tr = r.ReadMessage(wire);
                    int precision = 0;
                    while (tr.Next(out int tf, out int tw))
                    {
                        if (tf == F.PrecisionTimestampLiteral_Precision) precision = tr.ReadInt32(tw);
                        else if (tf == F.PrecisionTimestampLiteral_Value) lit.IntValue = tr.ReadInt64(tw);
                        else tr.Skip(tw);
                    }
                    lit.Type = new SType(field == F.Literal_PrecisionTimestamp ? TypeKind.PrecisionTimestamp : TypeKind.PrecisionTimestampTz, precision: precision);
                    break;
                }
                case F.Literal_IntervalDayToSecond:
                {
                    lit.Type = new SType(TypeKind.IntervalDay);
                    var ir = r.ReadMessage(wire);
                    // proto3 omits a zero precision, so only the legacy microseconds field marks the old encoding.
                    bool legacy = false;
                    long legacyMicros = 0;
                    while (ir.Next(out int iff, out int iw))
                    {
                        switch (iff)
                        {
                            case F.IntervalDayToSecond_Days: lit.Days = ir.ReadInt32(iw); break;
                            case F.IntervalDayToSecond_Seconds: lit.Seconds = ir.ReadInt32(iw); break;
                            case F.IntervalDayToSecond_Microseconds: legacyMicros = ir.ReadInt32(iw); legacy = true; break;
                            case F.IntervalDayToSecond_Precision: lit.SubsecondPrecision = ir.ReadInt32(iw); break;
                            case F.IntervalDayToSecond_Subseconds: lit.Subseconds = ir.ReadInt64(iw); break;
                            default: ir.Skip(iw); break;
                        }
                    }
                    if (legacy)
                    {
                        lit.Subseconds = legacyMicros;
                        lit.SubsecondPrecision = 6;
                    }
                    break;
                }
                case F.Literal_IntervalYearToMonth:
                {
                    lit.Type = new SType(TypeKind.IntervalYear);
                    var ir = r.ReadMessage(wire);
                    while (ir.Next(out int iff, out int iw))
                    {
                        if (iff == F.IntervalYearToMonth_Years) lit.Years = ir.ReadInt32(iw);
                        else if (iff == F.IntervalYearToMonth_Months) lit.Months = ir.ReadInt32(iw);
                        else ir.Skip(iw);
                    }
                    break;
                }
                case F.Literal_Decimal:
                {
                    var dr = r.ReadMessage(wire);
                    int precision = 0, scale = 0;
                    while (dr.Next(out int df, out int dw))
                    {
                        switch (df)
                        {
                            case F.DecimalLiteral_Value: lit.BytesValue = dr.ReadBytes(dw); break;
                            case F.DecimalLiteral_Precision: precision = dr.ReadInt32(dw); break;
                            case F.DecimalLiteral_Scale: scale = dr.ReadInt32(dw); break;
                            default: dr.Skip(dw); break;
                        }
                    }
                    if (lit.BytesValue == null || lit.BytesValue.Length != 16)
                        throw SubstraitTranslationException.MalformedPlan("Decimal literal value must be 16 bytes.");
                    lit.Type = new SType(TypeKind.Decimal, precision: precision, scale: scale);
                    break;
                }
                case F.Literal_Uuid:
                    lit.Type = new SType(TypeKind.Uuid);
                    lit.BytesValue = r.ReadBytes(wire);
                    if (lit.BytesValue.Length != 16)
                        throw SubstraitTranslationException.MalformedPlan("UUID literal must be 16 bytes.");
                    break;
                case F.Literal_Null:
                    lit.IsNull = true;
                    lit.Type = ReadType(r.ReadMessage(wire));
                    break;
                default:
                    throw SubstraitTranslationException.UnsupportedLiteral(field);
            }
        }
        if (!found)
            throw SubstraitTranslationException.MalformedPlan("Literal has no value.");
        if (!lit.IsNull)
            lit.Type = lit.Type.WithNullable(nullable);
        return lit;
    }

    static FieldRefExpr ReadFieldReference(ProtoReader r)
    {
        int index = -1;
        int stepsOut = 0;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.FieldReference_DirectReference:
                {
                    var sr = r.ReadMessage(wire);
                    while (sr.Next(out int sf, out int sw))
                    {
                        if (sf != F.ReferenceSegment_StructField)
                            throw SubstraitTranslationException.UnsupportedExpression("only struct field references are supported (no list or map element references).");
                        var fr = sr.ReadMessage(sw);
                        while (fr.Next(out int ff, out int fw))
                        {
                            if (ff == F.StructField_Field) index = fr.ReadInt32(fw);
                            else if (ff == F.StructField_Child)
                                throw SubstraitTranslationException.UnsupportedExpression("references into nested struct fields are not supported.");
                            else throw SubstraitTranslationException.UnexpectedField("ReferenceSegment.StructField", ff);
                        }
                        if (index < 0) index = 0;
                    }
                    break;
                }
                case F.FieldReference_RootReference:
                    r.Skip(wire);
                    break;
                case F.FieldReference_MaskedReference:
                    throw SubstraitTranslationException.UnsupportedExpression("masked field references are not supported.");
                case F.FieldReference_Expression:
                    throw SubstraitTranslationException.UnsupportedExpression("field references into expression results are not supported.");
                case F.FieldReference_OuterReference:
                {
                    var outer = r.ReadMessage(wire);
                    while (outer.Next(out int of, out int ow))
                    {
                        if (of == F.OuterReference_StepsOut) stepsOut = outer.ReadInt32(ow);
                        else if (of == F.OuterReference_RelReference)
                            throw SubstraitTranslationException.UnsupportedExpression("outer references by rel_reference are not supported; use steps_out.");
                        else throw SubstraitTranslationException.UnexpectedField("FieldReference.OuterReference", of);
                    }
                    if (stepsOut < 1)
                        throw SubstraitTranslationException.MalformedPlan("An outer reference must step out at least one subquery boundary.");
                    break;
                }
                case F.FieldReference_LambdaParameterReference:
                    throw SubstraitTranslationException.UnsupportedExpression("lambda parameter references are not supported.");
                default:
                    throw SubstraitTranslationException.UnexpectedField("FieldReference", field);
            }
        }
        if (index < 0)
            throw SubstraitTranslationException.UnsupportedExpression("FieldReference has no direct struct field reference.");
        return new FieldRefExpr { Index = index, StepsOut = stepsOut };
    }

    ScalarFunctionExpr ReadScalarFunction(ProtoReader r)
    {
        var expr = new ScalarFunctionExpr();
        uint functionRef = 0;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.ScalarFunction_FunctionReference: functionRef = r.ReadUInt32(wire); break;
                case F.ScalarFunction_Arguments: expr.Args.Add(ReadFunctionArgument(r.ReadMessage(wire))); break;
                case F.ScalarFunction_Args: expr.Args.Add(new FunctionArg { Value = ReadExpression(r.ReadMessage(wire)) }); break;
                case F.ScalarFunction_Options: expr.Options.Add(ReadFunctionOption(r.ReadMessage(wire))); break;
                case F.ScalarFunction_OutputType: expr.OutputType = ReadType(r.ReadMessage(wire)); break;
                default: throw SubstraitTranslationException.UnexpectedField("ScalarFunction", field);
            }
        }
        expr.Function = ResolveFunction(functionRef);
        return expr;
    }

    FunctionArg ReadFunctionArgument(ProtoReader r)
    {
        var arg = new FunctionArg();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.FunctionArgument_Enum: arg.Enum = r.ReadString(wire); break;
                case F.FunctionArgument_Type: arg.TypeArg = ReadType(r.ReadMessage(wire)); break;
                case F.FunctionArgument_Value: arg.Value = ReadExpression(r.ReadMessage(wire)); break;
                default: throw SubstraitTranslationException.UnexpectedField("FunctionArgument", field);
            }
        }
        if (arg.Value == null && arg.Enum == null && arg.TypeArg == null)
            throw SubstraitTranslationException.MalformedPlan("FunctionArgument is empty.");
        return arg;
    }

    static FunctionOptionValue ReadFunctionOption(ProtoReader r)
    {
        var opt = new FunctionOptionValue();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.FunctionOption_Name: opt.Name = r.ReadString(wire); break;
                case F.FunctionOption_Preference: opt.Preference.Add(r.ReadString(wire)); break;
                default: throw SubstraitTranslationException.UnexpectedField("FunctionOption", field);
            }
        }
        return opt;
    }

    IfThenExpr ReadIfThen(ProtoReader r)
    {
        var expr = new IfThenExpr();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.IfThen_Ifs:
                {
                    var cr = r.ReadMessage(wire);
                    Expr? cond = null, then = null;
                    while (cr.Next(out int cf, out int cw))
                    {
                        if (cf == F.IfClause_If) cond = ReadExpression(cr.ReadMessage(cw));
                        else if (cf == F.IfClause_Then) then = ReadExpression(cr.ReadMessage(cw));
                        else throw SubstraitTranslationException.UnexpectedField("IfThen.IfClause", cf);
                    }
                    if (cond == null || then == null)
                        throw SubstraitTranslationException.MalformedPlan("IfClause requires both 'if' and 'then'.");
                    expr.Clauses.Add((cond, then));
                    break;
                }
                case F.IfThen_Else: expr.Else = ReadExpression(r.ReadMessage(wire)); break;
                default: throw SubstraitTranslationException.UnexpectedField("IfThen", field);
            }
        }
        if (expr.Clauses.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("IfThen has no clauses.");
        return expr;
    }

    SwitchExpr ReadSwitch(ProtoReader r)
    {
        var expr = new SwitchExpr();
        Expr? match = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.Switch_Match: match = ReadExpression(r.ReadMessage(wire)); break;
                case F.Switch_Ifs:
                {
                    var cr = r.ReadMessage(wire);
                    LiteralExpr? value = null;
                    Expr? then = null;
                    while (cr.Next(out int cf, out int cw))
                    {
                        if (cf == F.SwitchIfValue_If) value = ReadLiteral(cr.ReadMessage(cw));
                        else if (cf == F.SwitchIfValue_Then) then = ReadExpression(cr.ReadMessage(cw));
                        else throw SubstraitTranslationException.UnexpectedField("SwitchExpression.IfValue", cf);
                    }
                    if (value == null || then == null)
                        throw SubstraitTranslationException.MalformedPlan("SwitchExpression.IfValue requires both 'if' and 'then'.");
                    expr.Cases.Add((value, then));
                    break;
                }
                case F.Switch_Else: expr.Else = ReadExpression(r.ReadMessage(wire)); break;
                default: throw SubstraitTranslationException.UnexpectedField("SwitchExpression", field);
            }
        }
        expr.Match = match ?? throw SubstraitTranslationException.MalformedPlan("SwitchExpression has no match expression.");
        if (expr.Cases.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("SwitchExpression has no cases.");
        return expr;
    }

    SingularOrListExpr ReadSingularOrList(ProtoReader r)
    {
        var expr = new SingularOrListExpr();
        Expr? value = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.SingularOrList_Value: value = ReadExpression(r.ReadMessage(wire)); break;
                case F.SingularOrList_Options: expr.Options.Add(ReadExpression(r.ReadMessage(wire))); break;
                default: throw SubstraitTranslationException.UnexpectedField("SingularOrList", field);
            }
        }
        expr.Value = value ?? throw SubstraitTranslationException.MalformedPlan("SingularOrList has no value.");
        return expr;
    }

    MultiOrListExpr ReadMultiOrList(ProtoReader r)
    {
        var expr = new MultiOrListExpr();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.MultiOrList_Value: expr.Values.Add(ReadExpression(r.ReadMessage(wire))); break;
                case F.MultiOrList_Options:
                {
                    var record = new List<Expr>();
                    var rr = r.ReadMessage(wire);
                    while (rr.Next(out int rf, out int rw))
                    {
                        if (rf == F.MultiOrListRecord_Fields) record.Add(ReadExpression(rr.ReadMessage(rw)));
                        else throw SubstraitTranslationException.UnexpectedField("MultiOrList.Record", rf);
                    }
                    if (record.Count != expr.Values.Count && expr.Values.Count > 0)
                        throw SubstraitTranslationException.MalformedPlan("MultiOrList record width does not match the number of values.");
                    expr.Options.Add(record);
                    break;
                }
                default: throw SubstraitTranslationException.UnexpectedField("MultiOrList", field);
            }
        }
        if (expr.Values.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("MultiOrList has no values.");
        foreach (var record in expr.Options)
        {
            if (record.Count != expr.Values.Count)
                throw SubstraitTranslationException.MalformedPlan("MultiOrList record width does not match the number of values.");
        }
        return expr;
    }

    SubqueryExpr ReadSubquery(ProtoReader r)
    {
        SubqueryExpr? expr = null;
        while (r.Next(out int field, out int wire))
        {
            if (expr != null)
                throw SubstraitTranslationException.MalformedPlan("Subquery contains more than one variant.");
            var sr = r.ReadMessage(wire);
            expr = new SubqueryExpr();
            Rel? input = null;
            switch (field)
            {
                case F.Subquery_Scalar:
                    expr.Kind = SubqueryKind.Scalar;
                    while (sr.Next(out int sf, out int sw))
                    {
                        if (sf == F.SubqueryScalar_Input) input = ReadRel(sr.ReadMessage(sw));
                        else throw SubstraitTranslationException.UnexpectedField("Subquery.Scalar", sf);
                    }
                    break;
                case F.Subquery_InPredicate:
                {
                    expr.Kind = SubqueryKind.In;
                    var needles = new List<Expr>();
                    while (sr.Next(out int sf, out int sw))
                    {
                        if (sf == F.InPredicate_Needles) needles.Add(ReadExpression(sr.ReadMessage(sw)));
                        else if (sf == F.InPredicate_Haystack) input = ReadRel(sr.ReadMessage(sw));
                        else throw SubstraitTranslationException.UnexpectedField("Subquery.InPredicate", sf);
                    }
                    if (needles.Count != 1)
                        throw SubstraitTranslationException.UnsupportedExpression($"IN subqueries with {needles.Count} needles are not supported; T-SQL's IN takes one value.");
                    expr.Needle = needles[0];
                    break;
                }
                case F.Subquery_SetPredicate:
                {
                    int op = 0;
                    while (sr.Next(out int sf, out int sw))
                    {
                        if (sf == F.SetPredicate_PredicateOp) op = sr.ReadInt32(sw);
                        else if (sf == F.SetPredicate_Tuples) input = ReadRel(sr.ReadMessage(sw));
                        else throw SubstraitTranslationException.UnexpectedField("Subquery.SetPredicate", sf);
                    }
                    if (op == F.PredicateOp_Unique)
                        throw SubstraitTranslationException.UnsupportedExpression("UNIQUE subqueries are not supported; T-SQL has no UNIQUE predicate.");
                    if (op != F.PredicateOp_Exists)
                        throw SubstraitTranslationException.MalformedPlan($"Subquery set predicate {op} is not valid.");
                    expr.Kind = SubqueryKind.Exists;
                    break;
                }
                case F.Subquery_SetComparison:
                    expr.Kind = SubqueryKind.Comparison;
                    while (sr.Next(out int sf, out int sw))
                    {
                        switch (sf)
                        {
                            case F.SetComparison_ReductionOp: expr.ReductionOp = sr.ReadInt32(sw); break;
                            case F.SetComparison_ComparisonOp: expr.ComparisonOp = sr.ReadInt32(sw); break;
                            case F.SetComparison_Left: expr.Needle = ReadExpression(sr.ReadMessage(sw)); break;
                            case F.SetComparison_Right: input = ReadRel(sr.ReadMessage(sw)); break;
                            default: throw SubstraitTranslationException.UnexpectedField("Subquery.SetComparison", sf);
                        }
                    }
                    if (expr.ReductionOp is not (F.ReductionOp_Any or F.ReductionOp_All))
                        throw SubstraitTranslationException.MalformedPlan($"Subquery set comparison reduction {expr.ReductionOp} is not valid.");
                    if (expr.ComparisonOp is < F.ComparisonOp_Eq or > F.ComparisonOp_Ge)
                        throw SubstraitTranslationException.MalformedPlan($"Subquery set comparison operator {expr.ComparisonOp} is not valid.");
                    if (expr.Needle == null)
                        throw SubstraitTranslationException.MalformedPlan("Subquery set comparison has no left expression.");
                    break;
                default:
                    throw SubstraitTranslationException.UnexpectedField("Subquery", field);
            }
            expr.Input = input ?? throw SubstraitTranslationException.MalformedPlan("Subquery has no relation.");
        }
        return expr ?? throw SubstraitTranslationException.MalformedPlan("Subquery is empty.");
    }

    CastExpr ReadCast(ProtoReader r)
    {
        var expr = new CastExpr();
        bool hasType = false;
        Expr? input = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.Cast_Type: expr.Type = ReadType(r.ReadMessage(wire)); hasType = true; break;
                case F.Cast_Input: input = ReadExpression(r.ReadMessage(wire)); break;
                case F.Cast_FailureBehavior: expr.FailureBehavior = r.ReadInt32(wire); break;
                default: throw SubstraitTranslationException.UnexpectedField("Cast", field);
            }
        }
        if (!hasType)
            throw SubstraitTranslationException.MalformedPlan("Cast has no target type.");
        expr.Input = input ?? throw SubstraitTranslationException.MalformedPlan("Cast has no input.");
        return expr;
    }

    static ContextVariableExpr ReadContextVariable(ProtoReader r)
    {
        var expr = new ContextVariableExpr();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.ContextVariable_CurrentTimestamp:
                case F.ContextVariable_CurrentTimezone:
                case F.ContextVariable_CurrentDate:
                    expr.Variable = field;
                    r.Skip(wire);
                    break;
                default:
                    throw SubstraitTranslationException.UnexpectedField("ExecutionContextVariable", field);
            }
        }
        if (expr.Variable == 0)
            throw SubstraitTranslationException.MalformedPlan("ExecutionContextVariable is empty.");
        return expr;
    }

    #endregion

    #region Types

    SType ReadType(ProtoReader r)
    {
        SType result = SType.Unknown;
        while (r.Next(out int field, out int wire))
        {
            var tr = r.ReadMessage(wire);
            result = field switch
            {
                F.Type_Bool => Simple(tr, TypeKind.Bool),
                F.Type_I8 => Simple(tr, TypeKind.I8),
                F.Type_I16 => Simple(tr, TypeKind.I16),
                F.Type_I32 => Simple(tr, TypeKind.I32),
                F.Type_I64 => Simple(tr, TypeKind.I64),
                F.Type_Fp32 => Simple(tr, TypeKind.Fp32),
                F.Type_Fp64 => Simple(tr, TypeKind.Fp64),
                F.Type_String => Simple(tr, TypeKind.String),
                F.Type_Binary => Simple(tr, TypeKind.Binary),
                F.Type_Date => Simple(tr, TypeKind.Date),
                F.Type_IntervalYear => Simple(tr, TypeKind.IntervalYear),
                F.Type_IntervalDay => Simple(tr, TypeKind.IntervalDay, F.IntervalDayType_Precision),
                F.Type_IntervalCompound => Simple(tr, TypeKind.IntervalCompound, F.IntervalDayType_Precision),
                F.Type_Uuid => Simple(tr, TypeKind.Uuid),
                F.Type_FixedChar => Parameterized(tr, TypeKind.FixedChar),
                F.Type_VarChar => Parameterized(tr, TypeKind.VarChar),
                F.Type_FixedBinary => Parameterized(tr, TypeKind.FixedBinary),
                F.Type_PrecisionTime => Parameterized(tr, TypeKind.PrecisionTime),
                F.Type_PrecisionTimestamp => Parameterized(tr, TypeKind.PrecisionTimestamp),
                F.Type_PrecisionTimestampTz => Parameterized(tr, TypeKind.PrecisionTimestampTz),
                F.Type_Decimal => DecimalType(tr),
                F.Type_Struct => Opaque(tr, TypeKind.Struct, F.StructType_Nullability),
                F.Type_List => Opaque(tr, TypeKind.List, F.StructType_Nullability),
                F.Type_Map => Opaque(tr, TypeKind.Map, 4),
                F.Type_Func => Opaque(tr, TypeKind.Func, 3),
                F.Type_Unbound => new SType(TypeKind.Unbound),
                F.Type_UserDefined => Opaque(tr, TypeKind.UserDefined, 3),
                F.Type_Alias => Opaque(tr, TypeKind.Alias, 2),
                _ => throw SubstraitTranslationException.UnexpectedField("Type", field),
            };
        }
        return result;
    }

    SType Simple(ProtoReader r, TypeKind kind, int precisionField = -1)
    {
        bool nullable = true;
        int precision = 0;
        while (r.Next(out int field, out int wire))
        {
            if (field == F.SimpleType_Nullability) nullable = r.ReadInt32(wire) != F.Nullability_Required;
            else if (field == precisionField) precision = r.ReadInt32(wire);
            else if (field == 1) RejectTypeVariation(r.ReadUInt32(wire));
            else r.Skip(wire);
        }
        return new SType(kind, nullable, precision);
    }

    SType Parameterized(ProtoReader r, TypeKind kind)
    {
        bool nullable = true;
        int precision = 0;
        while (r.Next(out int field, out int wire))
        {
            if (field == F.ParameterizedType_Length) precision = r.ReadInt32(wire);
            else if (field == F.ParameterizedType_Nullability) nullable = r.ReadInt32(wire) != F.Nullability_Required;
            else if (field == 2) RejectTypeVariation(r.ReadUInt32(wire));
            else r.Skip(wire);
        }
        return new SType(kind, nullable, precision);
    }

    SType DecimalType(ProtoReader r)
    {
        bool nullable = true;
        int precision = 0, scale = 0;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case F.DecimalType_Scale: scale = r.ReadInt32(wire); break;
                case F.DecimalType_Precision: precision = r.ReadInt32(wire); break;
                case F.DecimalType_Nullability: nullable = r.ReadInt32(wire) != F.Nullability_Required; break;
                case 3: RejectTypeVariation(r.ReadUInt32(wire)); break;
                default: r.Skip(wire); break;
            }
        }
        return new SType(TypeKind.Decimal, nullable, precision, scale);
    }

    static SType Opaque(ProtoReader r, TypeKind kind, int nullabilityField)
    {
        bool nullable = true;
        while (r.Next(out int field, out int wire))
        {
            if (field == nullabilityField && wire == ProtoReader.WireVarint) nullable = r.ReadInt32(wire) != F.Nullability_Required;
            else r.Skip(wire);
        }
        return new SType(kind, nullable);
    }

    /// <summary>
    /// Type variations declared in the plan may change function semantics, so they are rejected.
    /// References to undeclared variations are ignored: producers such as DataFusion use them to
    /// mark physical layouts (e.g. string views), which don't affect a T-SQL translation.
    /// </summary>
    void RejectTypeVariation(uint reference)
    {
        if (reference != 0 && _declaredTypeVariations.Contains(reference))
            throw SubstraitTranslationException.Unsupported($"Type variation {reference} declared by the plan is not supported.");
    }

    #endregion
}
