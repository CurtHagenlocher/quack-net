// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// Substrait -> DuckDB SQL: the statement's SubstraitPlan, written as one DuckDB query the server runs. The plans this
// is built for come from DuckDB itself (to_substrait, here the producer of the fabricator extension: plans of a DuckDB
// client pushing work to this server), so most function names are DuckDB's own and the dialect is the client's.
// Functions with no extension URN are DuckDB's own functions, declared by their DuckDB name. What cannot be written
// with DuckDB's exact meaning is refused (SubstraitTranslationException), never approximated.

using System.Globalization;
using System.Numerics;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using F = Quack.Adbc.Substrait.SubstraitFields;

namespace Quack.Adbc.Substrait;

/// <summary>
/// The server settings a plan's function options are checked against: what a division by zero and a float outside its
/// domain return is a session setting, and the plan says what the client's session does.
/// </summary>
internal sealed class DuckDbSettings
{
    /// <summary>The query that reads them as (name, value) rows.</summary>
    public const string Query =
        "SELECT name, CAST(value AS VARCHAR) AS value FROM duckdb_settings() "
        + "WHERE name IN ('ieee_floating_point_ops', 'error_on_division_by_zero')";

    public DuckDbSettings(bool ieeeFloatingPointOps, bool errorOnDivisionByZero)
    {
        IeeeFloatingPointOps = ieeeFloatingPointOps;
        ErrorOnDivisionByZero = errorOnDivisionByZero;
    }

    public bool IeeeFloatingPointOps { get; }
    public bool ErrorOnDivisionByZero { get; }

    /// <summary>Reads the rows of <see cref="Query"/>.</summary>
    public static DuckDbSettings Read(IArrowArrayStream stream)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
        {
            using (batch)
            {
                var names = (StringArray)batch.Column(0);
                var vals = (StringArray)batch.Column(1);
                for (int i = 0; i < batch.Length; i++)
                {
                    values[names.GetString(i)!] = vals.GetString(i) ?? "";
                }
            }
        }
        bool Flag(string name) => values.TryGetValue(name, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
        return new DuckDbSettings(Flag("ieee_floating_point_ops"), Flag("error_on_division_by_zero"));
    }
}

internal static class DuckDbSubstrait
{
    /// <summary>Translates a plan to one DuckDB query; its columns are the plan's, in order (named c0…).</summary>
    public static string ToSql(byte[] plan, DuckDbSettings settings)
    {
        if (plan.Length == 0)
            throw new SubstraitTranslationException("The Substrait plan is empty.");
        var decoder = new SubstraitPlanDecoder(plan);
        var root = decoder.DecodePlan();
        // the hint fabricator.repeated_reads needs nothing here: one DuckDB statement reads one snapshot
        var emitter = new DuckDbEmitter(settings, decoder.PlanOptimizations.Contains("fabricator.single_join_any_row"));
        return emitter.EmitRoot(root);
    }
}

internal sealed class DuckDbEmitter
{
    // the fabricator producer's own extensions: a function of the client's catalog (refused), date functions with no
    // Substrait declaration, and window functions with IGNORE NULLS
    private const string CatalogFunctionUrn = "extension:fabricator:catalog_function";
    private const string FabricatorDatetimeUrn = "extension:fabricator:functions_datetime";
    private const string FabricatorWindowUrn = "extension:fabricator:functions_window";

    private readonly DuckDbSettings _settings;
    private readonly bool _singleJoinAnyRow;
    private readonly Stack<IReadOnlyList<string>> _outer = new();
    // every named table read so far, as its FROM item ("<table> AS tN"): a DELETE / UPDATE finds its target's here
    private readonly List<(string Table, string Alias)> _reads = new();
    private int _alias;

    public DuckDbEmitter(DuckDbSettings settings, bool singleJoinAnyRow)
    {
        _settings = settings;
        _singleJoinAnyRow = singleJoinAnyRow;
    }

    // ── blocks ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One SELECT under construction: its columns are expressions over <see cref="From"/>.</summary>
    private sealed class Block
    {
        public string? From;
        public bool FromIsJoin;
        public List<string> Columns = new();
        public List<string> Where = new();
        public string? GroupBy;
        public bool Aggregated;
        public bool Windowed;
        public List<string> OrderBy = new();
        public string? Limit;
        public string? Offset;

        /// <summary>Rows straight from FROM / WHERE: a filter, a projection or an aggregate can go into the same SELECT.</summary>
        public bool IsPlain => !Aggregated && !Windowed && Limit == null && Offset == null;
    }

    private string NewAlias() => "t" + (_alias++).ToString(CultureInfo.InvariantCulture);

    private static string Col(int i) => "_" + i.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string Render(Block b, IReadOnlyList<string> names, bool withOrder)
    {
        var sql = new StringBuilder("SELECT ");
        if (b.Columns.Count == 0)
            sql.Append("NULL AS _none");
        else
            sql.Append(string.Join(", ", b.Columns.Select((c, i) => c + " AS " + names[i])));
        if (b.From != null)
            sql.Append(" FROM ").Append(b.From);
        if (b.Where.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", b.Where.Select(w => "(" + w + ")")));
        if (b.GroupBy != null)
            sql.Append(" GROUP BY ").Append(b.GroupBy);
        if (withOrder && b.OrderBy.Count > 0)
            sql.Append(" ORDER BY ").Append(string.Join(", ", b.OrderBy));
        if (b.Limit != null)
            sql.Append(" LIMIT ").Append(b.Limit);
        if (b.Offset != null)
            sql.Append(" OFFSET ").Append(b.Offset);
        return sql.ToString();
    }

    private static List<string> Names(int count) => Enumerable.Range(0, count).Select(Col).ToList();

    /// <summary>The block as a SELECT whose columns are _0…; its ORDER BY is kept (an order without a limit too).</summary>
    private static string Select(Block b) => Render(b, Names(b.Columns.Count), withOrder: true);

    /// <summary>
    /// The block as a derived table. With <paramref name="keepOrder"/>, its ORDER BY keys become hidden columns and the
    /// new block sorts by them, so a later fetch still takes the rows in that order.
    /// </summary>
    private Block Wrap(Block b, bool keepOrder)
    {
        string alias = NewAlias();
        var inner = new Block
        {
            From = b.From, FromIsJoin = b.FromIsJoin, Columns = new List<string>(b.Columns), Where = b.Where, GroupBy = b.GroupBy,
            Aggregated = b.Aggregated, Windowed = b.Windowed, OrderBy = b.OrderBy, Limit = b.Limit, Offset = b.Offset,
        };
        var outerOrder = new List<string>();
        if (keepOrder && b.OrderBy.Count > 0)
        {
            for (int i = 0; i < b.OrderBy.Count; i++)
            {
                var (expr, direction) = SplitOrder(b.OrderBy[i]);
                string name = "_o" + i.ToString(CultureInfo.InvariantCulture);
                inner.Columns.Add(expr);
                outerOrder.Add(alias + "." + name + " " + direction);
            }
        }
        var names = Names(b.Columns.Count);
        for (int i = b.Columns.Count; i < inner.Columns.Count; i++)
            names.Add("_o" + (i - b.Columns.Count).ToString(CultureInfo.InvariantCulture));
        string sql = Render(inner, names, withOrder: true);
        return new Block
        {
            From = "(" + sql + ") AS " + alias,
            Columns = Enumerable.Range(0, b.Columns.Count).Select(i => alias + "." + Col(i)).ToList(),
            OrderBy = outerOrder,
        };
    }

    // An ORDER BY item is "<expr> ASC NULLS FIRST"; the direction is the last three words.
    private static (string Expr, string Direction) SplitOrder(string item)
    {
        int cut = item.Length;
        for (int words = 0; words < 3; words++)
            cut = item.LastIndexOf(' ', cut - 1);
        return (item.Substring(0, cut), item.Substring(cut + 1));
    }

    // ── root ──────────────────────────────────────────────────────────────────────────────────────────────

    public string EmitRoot(RootRel root)
    {
        if (root.Input is WriteRel write)
            return EmitWrite(write);
        var block = EmitRel(root.Input);
        if (block.Columns.Count == 0)
            throw SubstraitTranslationException.Unsupported("The plan's result has no columns.");
        if (root.Names is { Count: > 0 } names && names.Count != block.Columns.Count)
            throw SubstraitTranslationException.MalformedPlan($"RelRoot has {names.Count} names but its input has {block.Columns.Count} columns.");
        var cols = Enumerable.Range(0, block.Columns.Count).Select(i => "c" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        return Render(block, cols, withOrder: true);
    }

    // ── writes ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A WriteRel, as the fabricator producer writes them: INSERT with the query that gives the rows (in the order of
    /// the written columns); DELETE with the query over the target that selects its rows; UPDATE with the query over
    /// the target that gives the new values, in the order of the assigned columns. The statement answers DuckDB's
    /// affected-row count; and the producer's MERGE (enhancement <c>fabricator.merge</c>, <see cref="EmitMerge"/>).
    /// Refused: RETURNING, rows moved by a DELETE inside an INSERT, a DELETE whose target is not read exactly once at
    /// the top of its query, an UPDATE joined with another table.
    /// </summary>
    private string EmitWrite(WriteRel write)
    {
        if (write.Output == F.WriteOutput_ModifiedRecords || write.Emit != null)
            throw SubstraitTranslationException.Unsupported("a write returning its rows (RETURNING)");
        if (write.Enhancement == F.Enhancement_FabricatorMerge)
            return EmitMerge(write);
        if (write.Enhancement != null)
            throw SubstraitTranslationException.Unsupported($"a write with the enhancement {write.Enhancement}");
        if (write.Input is WriteRel)
            throw SubstraitTranslationException.Unsupported("rows moved by a DELETE inside an INSERT");
        if (write.TableNames.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("A WriteRel names no table.");
        string target = string.Join(".", write.TableNames.Select(Quote));
        switch (write.Op)
        {
            case F.WriteOp_Insert:
            {
                if (write.ColumnNames.Count == 0 || write.ColumnNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != write.ColumnNames.Count)
                    throw SubstraitTranslationException.MalformedPlan("A WriteRel INSERT needs its columns, each once.");
                var block = EmitRel(write.Input);
                if (block.Columns.Count != write.ColumnNames.Count)
                    throw SubstraitTranslationException.MalformedPlan($"A WriteRel writes {write.ColumnNames.Count} columns from {block.Columns.Count}.");
                return "INSERT INTO " + target + " (" + string.Join(", ", write.ColumnNames.Select(Quote)) + ") " + Select(block);
            }
            case F.WriteOp_Delete or F.WriteOp_Update:
            {
                var block = EmitRel(write.Input);
                if (!block.IsPlain || block.From == null)
                    throw SubstraitTranslationException.Unsupported("a DELETE / UPDATE over a grouped, windowed or limited query");
                var reads = _reads.Where(r => r.Table == target).ToList();
                if (reads.Count != 1)
                    throw SubstraitTranslationException.Unsupported("a DELETE / UPDATE whose target is read more than once");
                string read = target + " AS " + reads[0].Alias;
                bool atTop = block.From == read || block.From.StartsWith(read + " ", StringComparison.Ordinal)
                    || block.From.Contains(" JOIN " + read + " ", StringComparison.Ordinal)
                    || block.From.EndsWith(" JOIN " + read, StringComparison.Ordinal);
                if (!atTop)
                    throw SubstraitTranslationException.Unsupported("a DELETE / UPDATE whose target is not read at the top of its query");
                string where = block.Where.Count > 0 ? " WHERE " + string.Join(" AND ", block.Where.Select(w => "(" + w + ")")) : "";
                if (write.Op == F.WriteOp_Delete)
                {
                    // the rows the query selects, by the target's row id (a join may select a row more than once)
                    return "DELETE FROM " + target + " WHERE rowid IN (SELECT " + reads[0].Alias + ".rowid FROM " + block.From + where + ")";
                }
                if (block.From != read)
                    throw SubstraitTranslationException.Unsupported("an UPDATE joined with another table");
                if (write.ColumnNames.Count == 0 || block.Columns.Count != write.ColumnNames.Count)
                    throw SubstraitTranslationException.MalformedPlan($"A WriteRel UPDATE assigns {write.ColumnNames.Count} columns from {block.Columns.Count}.");
                var assignments = write.ColumnNames.Select((c, i) => Quote(c) + " = " + block.Columns[i]);
                return "UPDATE " + read + " SET " + string.Join(", ", assignments) + where;
            }
            default:
                throw SubstraitTranslationException.Unsupported($"WriteRel operation {write.Op}");
        }
    }

    /// <summary>
    /// The producer's MERGE: its input is ONE query that already made the client's whole decision, a row per target /
    /// source pair with the target's key (<c>k:&lt;column&gt;</c>), <c>tp</c> (1 when a target row takes part), <c>op</c>
    /// (1 UPDATE, 2 DELETE, 3 INSERT), the new values (<c>u:&lt;column&gt;</c>) and the inserted ones
    /// (<c>i:&lt;column&gt;</c>). Written as a MERGE INTO that matches by the key (NULL keys match NULL ones). With
    /// <c>check_duplicates</c> a target row that two source rows would change fails, as in the client.
    /// </summary>
    private string EmitMerge(WriteRel write)
    {
        var block = EmitRel(write.Input);
        if (block.Columns.Count != write.ColumnNames.Count)
            throw SubstraitTranslationException.MalformedPlan($"The MERGE names {write.ColumnNames.Count} roles for {block.Columns.Count} columns.");
        string? tp = null, op = null;
        var keys = new List<(string Column, string Name)>();
        var updates = new List<(string Column, string Name)>();
        var inserts = new List<(string Column, string Name)>();
        for (int i = 0; i < write.ColumnNames.Count; i++)
        {
            string role = write.ColumnNames[i], name = "S." + Col(i);
            if (role == "tp")
                tp = name;
            else if (role == "op")
                op = name;
            else if (role.StartsWith("k:", StringComparison.Ordinal))
                keys.Add((role.Substring(2), name));
            else if (role.StartsWith("u:", StringComparison.Ordinal))
                updates.Add((role.Substring(2), name));
            else if (role.StartsWith("i:", StringComparison.Ordinal))
                inserts.Add((role.Substring(2), name));
            else
                throw SubstraitTranslationException.MalformedPlan($"The MERGE column role '{role}' is unknown.");
        }
        if (tp == null || op == null || keys.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("The MERGE has no key, tp or op column.");
        string source = Select(block);
        if (write.EnhancementValue == "check_duplicates")
        {
            // a target row two source rows would update or delete: the client's MERGE fails there
            string changes = op + " IN (1, 2) AND " + tp + " = 1";
            source = "SELECT S.*, CASE WHEN " + changes + " AND count(*) FILTER (WHERE " + changes + ") OVER (PARTITION BY "
                     + string.Join(", ", keys.Select(k => k.Name)) + ") > 1 THEN error('MERGE INTO command cannot affect "
                     + "the same target row more than once') END AS _check FROM (" + source + ") AS S";
        }
        string target = string.Join(".", write.TableNames.Select(Quote));
        var sql = new StringBuilder("MERGE INTO ").Append(target).Append(" AS T USING (").Append(source).Append(") AS S ON ")
            .Append(tp).Append(" = 1");
        foreach (var (column, name) in keys)
            sql.Append(" AND T.").Append(Quote(column)).Append(" IS NOT DISTINCT FROM ").Append(name);
        if (updates.Count > 0)
            sql.Append(" WHEN MATCHED AND ").Append(op).Append(" = 1 THEN UPDATE SET ")
               .Append(string.Join(", ", updates.Select(u => Quote(u.Column) + " = " + u.Name)));
        sql.Append(" WHEN MATCHED AND ").Append(op).Append(" = 2 THEN DELETE");
        if (inserts.Count > 0)
            sql.Append(" WHEN NOT MATCHED AND ").Append(op).Append(" = 3 THEN INSERT (")
               .Append(string.Join(", ", inserts.Select(i => Quote(i.Column)))).Append(") VALUES (")
               .Append(string.Join(", ", inserts.Select(i => i.Name))).Append(')');
        return sql.ToString();
    }

    // ── relations ─────────────────────────────────────────────────────────────────────────────────────────

    private Block EmitRel(Rel rel)
    {
        var block = rel switch
        {
            ReadRel r => EmitRead(r),
            FilterRel r => EmitFilter(r),
            ProjectRel r => EmitProject(r),
            FetchRel r => EmitFetch(EmitRel(r.Input), r.Offset, r.Count),
            SortRel r => EmitSort(EmitRel(r.Input), r.Sorts),
            TopNRel r => EmitTopN(r),
            AggregateRel r => EmitAggregate(r),
            JoinRel r => EmitJoin(r),
            CrossRel { Apply: true } => throw SubstraitTranslationException.Unsupported("table functions are not supported"),
            CrossRel r => EmitJoin(new JoinRel { Left = r.Left, Right = r.Right, Type = F.JoinType_Inner }),
            SetRel r => EmitSet(r),
            WindowRel r => EmitWindow(r),
            _ => throw SubstraitTranslationException.Unsupported($"relation {rel.GetType().Name}"),
        };
        if (rel.Emit != null)
        {
            var emitted = new List<string>(rel.Emit.Count);
            foreach (int index in rel.Emit)
            {
                if (index < 0 || index >= block.Columns.Count)
                    throw SubstraitTranslationException.MalformedPlan($"Emit references field {index} of {block.Columns.Count}.");
                emitted.Add(block.Columns[index]);
            }
            block.Columns = emitted;
        }
        return block;
    }

    private Block EmitRead(ReadRel rel)
    {
        if (rel.TableFunctionArgs != null)
            throw SubstraitTranslationException.Unsupported("table functions are not supported");
        string alias = NewAlias();
        var block = new Block();
        if (rel.VirtualRows != null)
        {
            int width = rel.ColumnNames.Count > 0 ? rel.ColumnNames.Count : rel.ColumnTypes.Count;
            if (rel.VirtualRows.Count == 0)
            {
                // no rows: typed NULLs under a false condition
                block.From = "(SELECT " + string.Join(", ", Enumerable.Range(0, width)
                    .Select(i => "CAST(NULL AS " + TypeName(rel.ColumnTypes[i]) + ") AS " + Col(i))) + " WHERE false) AS " + alias;
            }
            else
            {
                var rows = rel.VirtualRows.Select(row =>
                {
                    if (row.Count != width)
                        throw SubstraitTranslationException.MalformedPlan("A virtual table row has the wrong width.");
                    return "(" + string.Join(", ", row.Select((e, i) =>
                        i < rel.ColumnTypes.Count && rel.ColumnTypes[i].IsKnown
                            ? "CAST(" + RenderExpr(e, System.Array.Empty<string>()) + " AS " + TypeName(rel.ColumnTypes[i]) + ")"
                            : RenderExpr(e, System.Array.Empty<string>()))) + ")";
                });
                block.From = "(VALUES " + string.Join(", ", rows) + ") AS " + alias + "(" + string.Join(", ", Names(width)) + ")";
            }
            block.Columns = Enumerable.Range(0, width).Select(i => alias + "." + Col(i)).ToList();
        }
        else
        {
            if (rel.TableNames.Count == 0)
                throw SubstraitTranslationException.MalformedPlan("A read names no table.");
            string table = string.Join(".", rel.TableNames.Select(Quote));
            block.From = table + " AS " + alias;
            // a column the plan declares a string is read as VARCHAR: the client sees an ENUM as VARCHAR (Arrow has no
            // enum), so its order, min / max and comparisons must be the string's, not the ENUM's declared order (for a
            // VARCHAR column DuckDB drops the cast)
            block.Columns = rel.ColumnNames.Select((c, i) => i < rel.ColumnTypes.Count && rel.ColumnTypes[i].IsStringLike
                ? "CAST(" + alias + "." + Quote(c) + " AS VARCHAR)"
                : alias + "." + Quote(c)).ToList();
            _reads.Add((table, alias));
        }
        // the filters apply to the base schema, before the projection
        if (rel.Filter != null)
            block.Where.Add(RenderExpr(rel.Filter, block.Columns));
        if (rel.BestEffortFilter != null)
            block.Where.Add(RenderExpr(rel.BestEffortFilter, block.Columns));
        if (rel.Projection != null)
            block.Columns = rel.Projection.Select(i => i >= 0 && i < block.Columns.Count ? block.Columns[i]
                : throw SubstraitTranslationException.MalformedPlan($"The projection names field {i}.")).ToList();
        return block;
    }

    private Block EmitFilter(FilterRel rel)
    {
        var block = EmitRel(rel.Input);
        if (!block.IsPlain)
            block = Wrap(block, keepOrder: true);
        block.Where.Add(RenderExpr(rel.Condition, block.Columns));
        return block;
    }

    private Block EmitProject(ProjectRel rel)
    {
        var block = EmitRel(rel.Input);
        // a projection's expressions run per row in the same SELECT; over a window they would see the window's columns
        // as expressions, which DuckDB allows in a select list
        var scope = block.Columns;
        var added = rel.Expressions.Select(e => RenderExpr(e, scope)).ToList();
        block.Columns = scope.Concat(added).ToList();
        return block;
    }

    private Block EmitFetch(Block block, Expr? offset, Expr? count)
    {
        if (block.Limit != null || block.Offset != null)
            block = Wrap(block, keepOrder: true);
        if (count != null)
            block.Limit = RowCount(count, "count");
        if (offset != null)
            block.Offset = RowCount(offset, "offset");
        return block;
    }

    private string? RowCount(Expr expr, string what)
    {
        if (expr is not LiteralExpr { IsNull: false } lit || !lit.Type.IsInteger)
            throw SubstraitTranslationException.Unsupported($"a fetch {what} that is not an integer constant");
        if (lit.IntValue < 0)
            return what == "count" ? null : throw SubstraitTranslationException.Unsupported("a negative fetch offset");
        return lit.IntValue.ToString(CultureInfo.InvariantCulture);
    }

    private Block EmitSort(Block block, List<SortField> sorts)
    {
        if (block.Limit != null || block.Offset != null)
            block = Wrap(block, keepOrder: false);
        block.OrderBy = sorts.Select(s => OrderItem(s, block.Columns)).ToList();
        return block;
    }

    private string OrderItem(SortField sort, IReadOnlyList<string> scope)
    {
        string direction = sort.Direction switch
        {
            F.SortDirection_AscNullsFirst => "ASC NULLS FIRST",
            F.SortDirection_AscNullsLast => "ASC NULLS LAST",
            F.SortDirection_DescNullsFirst => "DESC NULLS FIRST",
            F.SortDirection_DescNullsLast => "DESC NULLS LAST",
            _ => throw SubstraitTranslationException.Unsupported($"sort direction {sort.Direction}"),
        };
        return KeyExpr(RenderExpr(sort.Expr, scope)) + " " + direction;
    }

    /// <summary>A bare integer constant in ORDER BY / GROUP BY is a column position in DuckDB; a cast keeps it a value.</summary>
    private static string KeyExpr(string sql) =>
        sql.Length > 0 && sql.All(ch => ch is (>= '0' and <= '9') or '-') ? "CAST(" + sql + " AS BIGINT)" : sql;

    private Block EmitTopN(TopNRel rel)
    {
        if (rel.WithTies)
            throw SubstraitTranslationException.Unsupported("WITH TIES");
        var block = EmitSort(EmitRel(rel.Input), rel.Sorts);
        return EmitFetch(block, rel.Offset, rel.Count);
    }

    private Block EmitAggregate(AggregateRel rel)
    {
        var block = EmitRel(rel.Input);
        if (!block.IsPlain)
            block = Wrap(block, keepOrder: false);
        block.OrderBy.Clear(); // an aggregate's output has no order
        var scope = block.Columns;
        var keys = rel.GroupingExpressions.Select(e => KeyExpr(RenderExpr(e, scope))).ToList();
        var measures = rel.Measures.Select(m => Aggregate(m, scope)).ToList();
        var columns = new List<string>();
        var sets = rel.GroupingSets;
        if (sets.Count <= 1)
        {
            var used = sets.Count == 1 ? sets[0].Select(i => keys[i]).Distinct(StringComparer.Ordinal).ToList() : new List<string>();
            block.GroupBy = used.Count == 0 ? null : string.Join(", ", used);
            columns.AddRange(keys);
            columns.AddRange(measures);
        }
        else
        {
            // GROUPING() of every key tells the sets apart; a set repeated, or a key under two indexes, cannot be
            var distinctKeys = keys.Distinct(StringComparer.Ordinal).ToList();
            if (distinctKeys.Count != keys.Count)
                throw SubstraitTranslationException.Unsupported("a grouping key repeated under another index");
            if (keys.Count > 62)
                throw SubstraitTranslationException.Unsupported("grouping sets over more than 62 keys");
            var masks = new List<long>();
            foreach (var set in sets)
            {
                long mask = 0;
                for (int k = 0; k < keys.Count; k++)
                    if (!set.Contains(k))
                        mask |= 1L << (keys.Count - 1 - k);
                if (masks.Contains(mask))
                    throw SubstraitTranslationException.Unsupported("a repeated grouping set");
                masks.Add(mask);
            }
            block.GroupBy = "GROUPING SETS (" + string.Join(", ", sets.Select(s => "(" + string.Join(", ", s.Select(i => keys[i])) + ")")) + ")";
            var index = new StringBuilder("CAST(CASE GROUPING(").Append(string.Join(", ", keys)).Append(')');
            for (int s = 0; s < masks.Count; s++)
                index.Append(" WHEN ").Append(masks[s].ToString(CultureInfo.InvariantCulture)).Append(" THEN ").Append(s);
            index.Append(" END AS INTEGER)");
            columns.AddRange(keys);
            columns.AddRange(measures);
            columns.Add(index.ToString());
        }
        block.Columns = columns;
        block.Aggregated = true;
        return block;
    }

    private string Aggregate(Measure measure, IReadOnlyList<string> scope)
    {
        var call = measure.Call;
        bool distinct = call.Invocation == F.AggregationInvocation_Distinct;
        if (call.Invocation is not (F.AggregationInvocation_Unspecified or F.AggregationInvocation_All or F.AggregationInvocation_Distinct))
            throw SubstraitTranslationException.Unsupported($"aggregate invocation {call.Invocation}");
        string name = AggregateName(call.Function, call.Options, call.Args.Count);
        var args = call.Args.Select(a => a.Value != null ? RenderExpr(a.Value, scope)
            : throw SubstraitTranslationException.Unsupported($"{call.Function.DisplayName}: an argument that is not a value")).ToList();
        var text = new StringBuilder(name).Append('(');
        if (name == "count" && args.Count == 0)
            text.Append('*');
        else
        {
            if (distinct)
                text.Append("DISTINCT ");
            text.Append(string.Join(", ", args));
            if (call.Sorts.Count > 0)
                text.Append(" ORDER BY ").Append(string.Join(", ", call.Sorts.Select(s => OrderItem(s, scope))));
        }
        text.Append(')');
        if (measure.Filter != null)
            text.Append(" FILTER (WHERE ").Append(RenderExpr(measure.Filter, scope)).Append(')');
        return text.ToString();
    }

    private static string AggregateName(FunctionDecl fn, List<FunctionOptionValue> options, int argCount)
    {
        if (fn.Urn == CatalogFunctionUrn)
            throw SubstraitTranslationException.Unsupported("catalog functions are not supported");
        if (options.Count > 0)
            throw SubstraitTranslationException.Unsupported($"{fn.DisplayName}: options {string.Join(", ", options.Select(o => o.Name))}");
        if (string.IsNullOrEmpty(fn.Urn))
            return Identifier(fn); // DuckDB's own aggregate, by its DuckDB name
        return fn.Name switch
        {
            // DuckDB's first and any_value both arrive as any_value; they differ on NULL
            "any_value" => throw SubstraitTranslationException.Unsupported("any_value (DuckDB's first or any_value)"),
            "std_dev" => "stddev",
            "grouping" => "GROUPING",
            _ => Identifier(fn),
        };
    }

    private static string Identifier(FunctionDecl fn)
    {
        string name = fn.Name;
        if (name.Length == 0 || !(name[0] is (>= 'a' and <= 'z') or '_') || !name.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            throw SubstraitTranslationException.Unsupported($"function '{fn.DisplayName}'");
        if (Volatile.Contains(name))
            throw SubstraitTranslationException.Unsupported($"volatile function '{name}'");
        return name;
    }

    private static readonly HashSet<string> Volatile = new(StringComparer.Ordinal)
    {
        "random", "uuid", "gen_random_uuid", "uuidv4", "uuidv7", "setseed", "nextval", "currval", "current_query",
        "current_setting", "now", "get_current_timestamp", "current_timestamp", "today", "current_date", "current_time",
        "transaction_timestamp", "txid_current", "current_transaction_id",
    };

    private Block EmitJoin(JoinRel rel)
    {
        var left = EmitRel(rel.Left);
        var right = EmitRel(rel.Right);
        switch (rel.Type)
        {
            case F.JoinType_RightSemi or F.JoinType_RightAnti or F.JoinType_RightMark or F.JoinType_RightSingle:
                throw SubstraitTranslationException.Unsupported($"join type {rel.Type}");
            case F.JoinType_LeftSemi or F.JoinType_LeftAnti:
                return SemiJoin(rel, left, right);
            case F.JoinType_LeftMark:
                return MarkJoin(rel, left, right);
            case F.JoinType_LeftSingle:
                return SingleJoin(rel, left, right);
        }
        string kind = rel.Type switch
        {
            F.JoinType_Inner => "JOIN",
            F.JoinType_Left => "LEFT JOIN",
            F.JoinType_Right => "RIGHT JOIN",
            F.JoinType_Outer => "FULL JOIN",
            _ => throw SubstraitTranslationException.Unsupported($"join type {rel.Type}"),
        };
        bool leftNullable = rel.Type is F.JoinType_Right or F.JoinType_Outer;
        bool rightNullable = rel.Type is F.JoinType_Left or F.JoinType_Outer;
        left = JoinSide(left, leftNullable, isRight: false);
        right = JoinSide(right, rightNullable, isRight: true);
        var scope = left.Columns.Concat(right.Columns).ToList();
        string on = rel.Expression == null ? "true" : RenderExpr(rel.Expression, scope);
        var block = new Block
        {
            From = left.From + " " + kind + " " + right.From + " ON " + on,
            FromIsJoin = true,
            Columns = scope,
            Where = left.Where.Concat(right.Where).ToList(),
        };
        if (rel.PostJoinFilter != null)
            block.Where.Add(RenderExpr(rel.PostJoinFilter, scope));
        return block;
    }

    /// <summary>A join input: its WHERE may move above the join only on a side the join does not pad with NULLs.</summary>
    /// <summary>
    /// A join input. Its WHERE may move above the join only on a side the join does not pad with NULLs; and on such a
    /// side every column must be a column of the FROM: a computed one (a constant, coalesce(x, 0)) written above the
    /// join would give its value where the join pads the row with NULL.
    /// </summary>
    private Block JoinSide(Block side, bool nullSupplying, bool isRight)
    {
        if (!side.IsPlain || side.From == null || (isRight && side.FromIsJoin)
            || (nullSupplying && (side.Where.Count > 0 || !side.Columns.All(IsColumnReference))))
            side = Wrap(side, keepOrder: false);
        side.OrderBy.Clear();
        return side;
    }

    private static readonly System.Text.RegularExpressions.Regex ColumnReference =
        new(@"^t[0-9]+\.(_[0-9]+|""([^""]|"""")*"")$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsColumnReference(string sql) => ColumnReference.IsMatch(sql);

    private Block SemiJoin(JoinRel rel, Block left, Block right)
    {
        left = JoinSide(left, nullSupplying: false, isRight: false);
        right = JoinSide(right, nullSupplying: true, isRight: true);
        var scope = left.Columns.Concat(right.Columns).ToList();
        string on = rel.Expression == null ? "true" : RenderExpr(rel.Expression, scope);
        if (rel.PostJoinFilter != null)
            on = "(" + on + ") AND (" + RenderExpr(rel.PostJoinFilter, scope) + ")";
        return new Block
        {
            From = left.From + (rel.Type == F.JoinType_LeftSemi ? " SEMI JOIN " : " ANTI JOIN ") + right.From + " ON " + on,
            FromIsJoin = true,
            Columns = left.Columns,
            Where = left.Where,
        };
    }

    /// <summary>LEFT_MARK: the left rows plus whether a right row matches — true, else NULL if a comparison was NULL, else false.</summary>
    private Block MarkJoin(JoinRel rel, Block left, Block right)
    {
        if (rel.PostJoinFilter != null)
            throw SubstraitTranslationException.Unsupported("a mark join with a post-join filter");
        if (!left.IsPlain)
            left = Wrap(left, keepOrder: false);
        right = JoinSide(right, nullSupplying: true, isRight: true);
        var scope = left.Columns.Concat(right.Columns).ToList();
        string on = rel.Expression == null ? "true" : RenderExpr(rel.Expression, scope);
        string from = right.From!;
        string mark = $"CASE WHEN EXISTS (SELECT 1 FROM {from} WHERE {on}) THEN true " +
                      $"WHEN EXISTS (SELECT 1 FROM {from} WHERE ({on}) IS NULL) THEN NULL ELSE false END";
        left.Columns = left.Columns.Append(mark).ToList();
        return left;
    }

    /// <summary>LEFT_SINGLE: a scalar subquery per right column — DuckDB raises an error when more than one row matches.</summary>
    private Block SingleJoin(JoinRel rel, Block left, Block right)
    {
        if (rel.PostJoinFilter != null)
            throw SubstraitTranslationException.Unsupported("a single join with a post-join filter");
        if (_singleJoinAnyRow && right.Columns.Count != 1)
            throw SubstraitTranslationException.Unsupported("a single join taking any row of several columns");
        if (!left.IsPlain)
            left = Wrap(left, keepOrder: false);
        right = JoinSide(right, nullSupplying: true, isRight: true);
        var scope = left.Columns.Concat(right.Columns).ToList();
        string on = rel.Expression == null ? "true" : RenderExpr(rel.Expression, scope);
        string limit = _singleJoinAnyRow ? " LIMIT 1" : "";
        left.Columns = left.Columns.Concat(right.Columns.Select(c => $"(SELECT {c} FROM {right.From} WHERE {on}{limit})")).ToList();
        return left;
    }

    private Block EmitSet(SetRel rel)
    {
        if (rel.Inputs.Count == 0)
            throw SubstraitTranslationException.MalformedPlan("A set operation has no inputs.");
        var branches = rel.Inputs.Select(EmitRel).ToList();
        int width = branches[0].Columns.Count;
        if (branches.Any(b => b.Columns.Count != width))
            throw SubstraitTranslationException.MalformedPlan("Set operation inputs differ in width.");
        string op = rel.Op switch
        {
            F.SetOp_UnionAll => "UNION ALL",
            F.SetOp_UnionDistinct => "UNION",
            F.SetOp_MinusPrimary => "EXCEPT",
            F.SetOp_MinusPrimaryAll when branches.Count == 2 => "EXCEPT ALL",
            F.SetOp_IntersectionPrimary when branches.Count == 2 => "INTERSECT",
            F.SetOp_IntersectionMultisetAll when branches.Count == 2 => "INTERSECT ALL",
            _ => throw SubstraitTranslationException.Unsupported($"set operation {rel.Op} over {branches.Count} inputs"),
        };
        string alias = NewAlias();
        string sql = string.Join(" " + op + " ", branches.Select(b => "(" + Select(b) + ")"));
        if (branches.Count == 1 && op is "UNION" or "EXCEPT")
            sql = "SELECT DISTINCT * FROM (" + sql + ")";
        return new Block
        {
            From = "(" + sql + ") AS " + alias,
            Columns = Enumerable.Range(0, width).Select(i => alias + "." + Col(i)).ToList(),
        };
    }

    private Block EmitWindow(WindowRel rel)
    {
        var block = EmitRel(rel.Input);
        if (block.Windowed || block.Limit != null || block.Offset != null)
            block = Wrap(block, keepOrder: true);
        var scope = block.Columns;
        string over = "";
        if (rel.Partitions.Count > 0)
            over += "PARTITION BY " + string.Join(", ", rel.Partitions.Select(p => KeyExpr(RenderExpr(p, scope))));
        if (rel.Sorts.Count > 0)
            over += (over.Length > 0 ? " " : "") + "ORDER BY " + string.Join(", ", rel.Sorts.Select(s => OrderItem(s, scope)));
        var functions = rel.Functions.Select(f => WindowFunction(f, scope, over)).ToList();
        block.Columns = scope.Concat(functions).ToList();
        block.Windowed = true;
        return block;
    }

    private static readonly HashSet<string> Ranking = new(StringComparer.Ordinal)
    {
        "rank", "dense_rank", "percent_rank", "cume_dist", "row_number", "ntile", "lag", "lead",
    };

    private string WindowFunction(WindowCall call, IReadOnlyList<string> scope, string over)
    {
        string name = call.Function.Name;
        bool ignoreNulls = false;
        if (call.Function.Urn == FabricatorWindowUrn && name.EndsWith("_ignore_nulls", StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - "_ignore_nulls".Length);
            ignoreNulls = true;
        }
        if (call.Options.Count > 0)
            throw SubstraitTranslationException.Unsupported($"{call.Function.DisplayName}: options");
        if (call.Invocation == F.AggregationInvocation_Distinct)
            throw SubstraitTranslationException.Unsupported("DISTINCT window functions");
        string fn = name switch
        {
            "any_value" => throw SubstraitTranslationException.Unsupported("any_value"),
            "std_dev" => "stddev",
            _ => Identifier(new FunctionDecl { Name = name, Urn = call.Function.Urn }),
        };
        var args = call.Args.Select(a => a.Value != null ? RenderExpr(a.Value, scope)
            : throw SubstraitTranslationException.Unsupported($"{name}: an argument that is not a value")).ToList();
        string text = fn + "(" + (fn == "count" && args.Count == 0 ? "*" : string.Join(", ", args)) + (ignoreNulls ? " IGNORE NULLS" : "") + ")";
        string frame = Ranking.Contains(fn) ? "" : Frame(call, scope);
        string clause = over + (frame.Length > 0 ? (over.Length > 0 ? " " : "") + frame : "");
        return text + " OVER (" + clause + ")";
    }

    private string Frame(WindowCall call, IReadOnlyList<string> scope)
    {
        if (call.Lower == null || call.Upper == null)
            throw SubstraitTranslationException.MalformedPlan("A window function has no bounds.");
        string unit = call.BoundsType switch
        {
            F.BoundsType_Rows => "ROWS",
            F.BoundsType_Range or F.BoundsType_Unspecified => "RANGE",
            _ => throw SubstraitTranslationException.Unsupported($"window bounds type {call.BoundsType}"),
        };
        string Bound(WindowBound bound, bool lower)
        {
            return bound.Kind switch
            {
                F.WindowBound_Unbounded => lower ? "UNBOUNDED PRECEDING" : "UNBOUNDED FOLLOWING",
                F.WindowBound_CurrentRow => "CURRENT ROW",
                F.WindowBound_Preceding => Offset(bound) + " PRECEDING",
                F.WindowBound_Following => Offset(bound) + " FOLLOWING",
                _ => throw SubstraitTranslationException.Unsupported($"window bound {bound.Kind}"),
            };
        }
        string Offset(WindowBound bound) => bound.Offset != null ? RenderExpr(bound.Offset, scope)
            : throw SubstraitTranslationException.Unsupported("a window bound without an offset");
        return unit + " BETWEEN " + Bound(call.Lower, lower: true) + " AND " + Bound(call.Upper, lower: false);
    }

    // ── expressions ───────────────────────────────────────────────────────────────────────────────────────

    private string RenderExpr(Expr expr, IReadOnlyList<string> scope) => expr switch
    {
        LiteralExpr lit => Literal(lit),
        FieldRefExpr fr => Field(fr, scope),
        ScalarFunctionExpr sf => Scalar(sf, scope),
        CastExpr c => Cast(c, scope),
        IfThenExpr it => "CASE " + string.Join(" ", it.Clauses.Select(c => "WHEN " + RenderExpr(c.If, scope) + " THEN " + RenderExpr(c.Then, scope)))
                         + (it.Else != null ? " ELSE " + RenderExpr(it.Else, scope) : "") + " END",
        SwitchExpr sw => "CASE " + RenderExpr(sw.Match, scope) + " " + string.Join(" ", sw.Cases.Select(c => "WHEN " + Literal(c.If) + " THEN " + RenderExpr(c.Then, scope)))
                         + (sw.Else != null ? " ELSE " + RenderExpr(sw.Else, scope) : "") + " END",
        SingularOrListExpr ol => "(" + RenderExpr(ol.Value, scope) + " IN (" + string.Join(", ", ol.Options.Select(o => RenderExpr(o, scope))) + "))",
        MultiOrListExpr ml => MultiOrList(ml, scope),
        SubqueryExpr sq => Subquery(sq, scope),
        ContextVariableExpr => throw SubstraitTranslationException.Unsupported("context variables (current_date …)"),
        _ => throw SubstraitTranslationException.UnsupportedExpression(expr.GetType().Name),
    };

    private string Field(FieldRefExpr fr, IReadOnlyList<string> scope)
    {
        var target = scope;
        if (fr.StepsOut > 0)
        {
            if (fr.StepsOut > _outer.Count)
                throw SubstraitTranslationException.MalformedPlan($"An outer reference crosses {fr.StepsOut} subquery boundaries.");
            target = _outer.ElementAt(fr.StepsOut - 1);
        }
        if (fr.Index < 0 || fr.Index >= target.Count)
            throw SubstraitTranslationException.MalformedPlan($"Field {fr.Index} of {target.Count}.");
        return target[fr.Index];
    }

    private string MultiOrList(MultiOrListExpr ml, IReadOnlyList<string> scope)
    {
        var values = ml.Values.Select(v => RenderExpr(v, scope)).ToList();
        var options = ml.Options.Select(option =>
        {
            if (option.Count != values.Count)
                throw SubstraitTranslationException.MalformedPlan("A multi-or-list option has the wrong width.");
            return "(" + string.Join(" AND ", option.Select((o, i) => "(" + values[i] + " = " + RenderExpr(o, scope) + ")")) + ")";
        });
        return "(" + string.Join(" OR ", options) + ")";
    }

    private string Subquery(SubqueryExpr sq, IReadOnlyList<string> scope)
    {
        _outer.Push(scope);
        string sql;
        try
        {
            sql = Select(EmitRel(sq.Input));
        }
        finally
        {
            _outer.Pop();
        }
        switch (sq.Kind)
        {
            case SubqueryKind.Scalar:
                return "(" + sql + ")";
            case SubqueryKind.Exists:
                return "EXISTS (" + sql + ")";
            case SubqueryKind.In:
                return "(" + RenderExpr(sq.Needle!, scope) + " IN (" + sql + "))";
            case SubqueryKind.Comparison:
                string op = sq.ComparisonOp switch
                {
                    F.ComparisonOp_Eq => "=", F.ComparisonOp_Ne => "<>", F.ComparisonOp_Lt => "<",
                    F.ComparisonOp_Gt => ">", F.ComparisonOp_Le => "<=", F.ComparisonOp_Ge => ">=",
                    _ => throw SubstraitTranslationException.Unsupported($"comparison {sq.ComparisonOp}"),
                };
                string reduction = sq.ReductionOp == F.ReductionOp_All ? "ALL" : sq.ReductionOp == F.ReductionOp_Any ? "ANY"
                    : throw SubstraitTranslationException.Unsupported($"reduction {sq.ReductionOp}");
                return "(" + RenderExpr(sq.Needle!, scope) + " " + op + " " + reduction + " (" + sql + "))";
            default:
                throw SubstraitTranslationException.Unsupported($"subquery {sq.Kind}");
        }
    }

    private string Cast(CastExpr c, IReadOnlyList<string> scope)
    {
        string op = c.FailureBehavior switch
        {
            F.FailureBehavior_ReturnNull => "TRY_CAST",
            _ => "CAST",
        };
        return op + "(" + RenderExpr(c.Input, scope) + " AS " + TypeName(c.Type) + ")";
    }

    // ── scalar functions ──────────────────────────────────────────────────────────────────────────────────

    private string Scalar(ScalarFunctionExpr sf, IReadOnlyList<string> scope)
    {
        var fn = sf.Function;
        if (fn.Urn == CatalogFunctionUrn)
            throw SubstraitTranslationException.Unsupported("catalog functions are not supported");
        string V(int i) => i < sf.Args.Count && sf.Args[i].Value is { } v ? RenderExpr(v, scope)
            : throw SubstraitTranslationException.MalformedPlan($"Argument {i} of '{fn.DisplayName}' must be a value.");
        string E(int i) => i < sf.Args.Count && sf.Args[i].Enum is { } e ? e
            : throw SubstraitTranslationException.MalformedPlan($"Argument {i} of '{fn.DisplayName}' must be an enumeration.");
        IEnumerable<string> All() => Enumerable.Range(0, sf.Args.Count).Select(V);
        string Infix(string op) => "(" + string.Join(" " + op + " ", All()) + ")";
        var sig = (fn.Signature ?? "").Split('_');

        CheckOptions(sf, sig);

        // a function with no extension is DuckDB's own (the producer declares it by its DuckDB name): written as is
        if (string.IsNullOrEmpty(fn.Urn))
        {
            if (sf.Args.Any(a => a.Value == null))
                throw SubstraitTranslationException.Unsupported($"{fn.DisplayName}: an enumeration argument");
            return Identifier(fn) + "(" + string.Join(", ", All()) + ")";
        }

        if (fn.Urn == FabricatorDatetimeUrn)
        {
            return fn.Name switch
            {
                "date_diff" => "date_diff('" + E(0).ToLowerInvariant() + "', " + V(1) + ", " + V(2) + ")",
                "last_day" => "last_day(" + V(0) + ")",
                "make_date" => "make_date(" + V(0) + ", " + V(1) + ", " + V(2) + ")",
                _ => throw SubstraitTranslationException.UnsupportedFunction(fn.DisplayName),
            };
        }

        switch (fn.Name)
        {
            case "add": return Infix("+");
            case "subtract": return Infix("-");
            case "multiply": return Infix("*");
            case "negate": return "(- " + V(0) + ")";
            case "divide":
                if (sig.Length > 0 && sig[0] is "i8" or "i16" or "i32" or "i64")
                    return Infix("//");
                if (sig.Length > 0 && sig[0] is "fp32" or "fp64")
                    return Infix("/");
                throw SubstraitTranslationException.Unsupported($"divide over {fn.Signature}");
            case "modulus": return Infix("%");
            case "equal": return Infix("=");
            case "not_equal": return Infix("<>");
            case "lt": return Infix("<");
            case "lte": return Infix("<=");
            case "gt": return Infix(">");
            case "gte": return Infix(">=");
            case "is_distinct_from": return Infix("IS DISTINCT FROM");
            case "is_not_distinct_from": return Infix("IS NOT DISTINCT FROM");
            case "between": return "(" + V(0) + " BETWEEN " + V(1) + " AND " + V(2) + ")";
            case "and": return Infix("AND");
            case "or": return Infix("OR");
            case "not": return "(NOT " + V(0) + ")";
            case "is_null": return "(" + V(0) + " IS NULL)";
            case "is_not_null": return "(" + V(0) + " IS NOT NULL)";
            case "is_true": return "(" + V(0) + " IS TRUE)";
            case "is_false": return "(" + V(0) + " IS FALSE)";
            case "is_not_true": return "(" + V(0) + " IS NOT TRUE)";
            case "is_not_false": return "(" + V(0) + " IS NOT FALSE)";
            case "like":
                return sf.Args.Count == 3 ? "(" + V(0) + " LIKE " + V(1) + " ESCAPE " + V(2) + ")" : "(" + V(0) + " LIKE " + V(1) + ")";
            case "concat":
                return Option(sf, "null_handling") == "ACCEPT_NULLS" ? Infix("||") : "concat(" + string.Join(", ", All()) + ")";
            case "char_length": return "length(" + V(0) + ")";
            case "octet_length": return (sig.Length > 0 && sig[0] is "str" or "vchar" or "fchar" ? "strlen(" : "octet_length(") + V(0) + ")";
            case "ends_with": return "suffix(" + V(0) + ", " + V(1) + ")";
            case "is_nan": return "isnan(" + V(0) + ")";
            case "is_finite": return "isfinite(" + V(0) + ")";
            case "is_infinite": return "isinf(" + V(0) + ")";
            case "bitwise_and": return Infix("&");
            case "bitwise_or": return Infix("|");
            case "bitwise_xor": return "xor(" + V(0) + ", " + V(1) + ")";
            case "least_skip_null": return "least(" + string.Join(", ", All()) + ")";
            case "greatest_skip_null": return "greatest(" + string.Join(", ", All()) + ")";
            case "least" or "greatest":
                throw SubstraitTranslationException.Unsupported($"{fn.Name} returning NULL for any NULL argument");
            case "extract": return Extract(sf, E, V);
            case "round_calendar": return RoundCalendar(sf, E, V);
            case "coalesce": return "coalesce(" + string.Join(", ", All()) + ")";
        }
        if (sf.Args.Any(a => a.Value == null))
            throw SubstraitTranslationException.Unsupported($"{fn.DisplayName}: an enumeration argument");
        return Identifier(fn) + "(" + string.Join(", ", All()) + ")";
    }

    private static string? Option(ScalarFunctionExpr sf, string name) =>
        sf.Options.FirstOrDefault(o => o.Name == name)?.Preference.FirstOrDefault();

    /// <summary>Options that change a result must be what this server does; others are refused.</summary>
    private void CheckOptions(ScalarFunctionExpr sf, string[] sig)
    {
        foreach (var option in sf.Options)
        {
            string? want = option.Preference.FirstOrDefault();
            bool ok = option.Name switch
            {
                "null_handling" => sf.Function.Name == "concat" && want is "ACCEPT_NULLS" or "IGNORE_NULLS",
                "overflow" => want is "ERROR",
                "on_division_by_zero" => want == DivisionByZero(sig),
                "on_domain_error" => want == (_settings.IeeeFloatingPointOps || sf.Function.Name is "power" or "pow" ? "NAN" : "ERROR"),
                "on_log_zero" => want == (_settings.IeeeFloatingPointOps ? "MINUS_INFINITY" : "ERROR"),
                _ => false,
            };
            if (!ok)
                throw SubstraitTranslationException.Unsupported(
                    $"{sf.Function.DisplayName}: option {option.Name} = {want} is not what this server does");
        }
    }

    // DuckDB binds a division by zero from its settings: inf / nan for a float `/` under ieee_floating_point_ops,
    // else an error, or NULL without error_on_division_by_zero.
    private string DivisionByZero(string[] sig)
    {
        bool floating = sig.Length > 0 && sig[0] is "fp32" or "fp64";
        if (floating && _settings.IeeeFloatingPointOps)
            return "IEEE";
        return _settings.ErrorOnDivisionByZero ? "ERROR" : "NULL";
    }

    private static string Extract(ScalarFunctionExpr sf, Func<int, string> e, Func<int, string> v)
    {
        string component = e(0);
        bool indexed = sf.Args.Count == 3;
        string? indexing = indexed ? e(1) : null;
        string value = v(indexed ? 2 : 1);
        string fn = (component, indexing) switch
        {
            ("YEAR", null) => "year",
            ("ISO_YEAR", null) => "isoyear",
            ("QUARTER", "ONE") => "quarter",
            ("MONTH", "ONE") => "month",
            ("DAY", "ONE") => "day",
            ("DAY_OF_YEAR", "ONE") => "dayofyear",
            ("ISO_WEEK", "ONE") => "week",
            ("MONDAY_DAY_OF_WEEK", "ONE") => "isodow",
            ("SUNDAY_DAY_OF_WEEK", "ZERO") => "dayofweek",
            ("HOUR", null) => "hour",
            ("MINUTE", null) => "minute",
            ("SECOND", null) => "second",
            _ => throw SubstraitTranslationException.Unsupported($"extract {component} {indexing}"),
        };
        return fn + "(" + value + ")";
    }

    private static string RoundCalendar(ScalarFunctionExpr sf, Func<int, string> e, Func<int, string> v)
    {
        if (sf.Args.Count != 5 || e(1) != "FLOOR")
            throw SubstraitTranslationException.Unsupported("round_calendar other than FLOOR");
        if (sf.Args[4].Value is not LiteralExpr { IsNull: false } multiple || !multiple.Type.IsInteger)
            throw SubstraitTranslationException.Unsupported("round_calendar with a multiple that is not a constant");
        string part = (e(2), e(3), multiple.IntValue) switch
        {
            ("YEAR", "YEAR", 1) => "year",
            ("MONTH", "YEAR", 3) => "quarter",
            ("MONTH", "YEAR", 1) => "month",
            ("WEEK", "ISO_WEEK", 1) => "week",
            ("DAY", "MONTH", 1) => "day",
            ("HOUR", "DAY", 1) => "hour",
            ("MINUTE", "HOUR", 1) => "minute",
            ("SECOND", "MINUTE", 1) => "second",
            _ => throw SubstraitTranslationException.Unsupported("round_calendar unit"),
        };
        return "date_trunc('" + part + "', " + v(0) + ")";
    }

    // ── literals and types ────────────────────────────────────────────────────────────────────────────────

    private static string TypeName(SType type) => type.Kind switch
    {
        TypeKind.Bool => "BOOLEAN",
        TypeKind.I8 => "TINYINT",
        TypeKind.I16 => "SMALLINT",
        TypeKind.I32 => "INTEGER",
        TypeKind.I64 => "BIGINT",
        TypeKind.Fp32 => "FLOAT",
        TypeKind.Fp64 => "DOUBLE",
        TypeKind.String or TypeKind.VarChar or TypeKind.FixedChar => "VARCHAR",
        TypeKind.Binary or TypeKind.FixedBinary => "BLOB",
        TypeKind.Date => "DATE",
        TypeKind.PrecisionTime when type.Precision <= 6 => "TIME",
        TypeKind.PrecisionTimestamp => type.Precision switch
        {
            0 => "TIMESTAMP_S",
            3 => "TIMESTAMP_MS",
            6 => "TIMESTAMP",
            9 => "TIMESTAMP_NS",
            _ => throw SubstraitTranslationException.Unsupported($"timestamp precision {type.Precision}"),
        },
        TypeKind.PrecisionTimestampTz when type.Precision == 6 => "TIMESTAMPTZ",
        TypeKind.Decimal => $"DECIMAL({type.Precision},{type.Scale})",
        TypeKind.Uuid => "UUID",
        TypeKind.IntervalYear or TypeKind.IntervalDay or TypeKind.IntervalCompound => "INTERVAL",
        _ => throw SubstraitTranslationException.Unsupported($"type {type}"),
    };

    private static string Literal(LiteralExpr lit)
    {
        if (lit.IsNull)
        {
            // a NULL with no type (the producer writes CAST(NULL AS …) that way): DuckDB types it from where it stands
            return lit.Type.IsKnown ? "CAST(NULL AS " + TypeName(lit.Type) + ")" : "NULL";
        }
        var inv = CultureInfo.InvariantCulture;
        switch (lit.Type.Kind)
        {
            case TypeKind.Bool:
                return lit.BoolValue ? "true" : "false";
            case TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64:
                return "CAST(" + lit.IntValue.ToString(inv) + " AS " + TypeName(lit.Type) + ")";
            case TypeKind.Fp32 or TypeKind.Fp64:
            {
                double d = lit.FloatValue;
                string text = double.IsNaN(d) ? "'nan'" : double.IsPositiveInfinity(d) ? "'inf'" : double.IsNegativeInfinity(d) ? "'-inf'"
                    : lit.Type.Kind == TypeKind.Fp32 ? "'" + ((float)d).ToString("R", inv) + "'" : "'" + d.ToString("R", inv) + "'";
                return "CAST(" + text + " AS " + TypeName(lit.Type) + ")";
            }
            case TypeKind.String or TypeKind.VarChar or TypeKind.FixedChar:
                return "'" + (lit.StringValue ?? "").Replace("'", "''") + "'";
            case TypeKind.Binary or TypeKind.FixedBinary:
                return "from_hex('" + Convert.ToHexString(lit.BytesValue ?? System.Array.Empty<byte>()) + "')";
            case TypeKind.Date:
                return "(DATE '1970-01-01' + CAST(" + lit.IntValue.ToString(inv) + " AS INTEGER))";
            case TypeKind.PrecisionTime:
            {
                long micros = lit.IntValue * Pow10(6 - lit.Type.Precision);
                return "(TIME '00:00:00' + to_microseconds(CAST(" + micros.ToString(inv) + " AS BIGINT)))";
            }
            case TypeKind.PrecisionTimestamp:
            {
                if (lit.Type.Precision == 9)
                    return "make_timestamp_ns(CAST(" + lit.IntValue.ToString(inv) + " AS BIGINT))";
                if (lit.Type.Precision is not (0 or 3 or 6))
                    throw SubstraitTranslationException.Unsupported($"timestamp precision {lit.Type.Precision}");
                long micros = lit.IntValue * Pow10(6 - lit.Type.Precision);
                return "CAST(make_timestamp(CAST(" + micros.ToString(inv) + " AS BIGINT)) AS " + TypeName(lit.Type) + ")";
            }
            case TypeKind.Decimal:
            {
                var unscaled = new BigInteger(lit.BytesValue!, isUnsigned: false, isBigEndian: false);
                string digits = BigInteger.Abs(unscaled).ToString(inv).PadLeft(lit.Type.Scale + 1, '0');
                string text = (unscaled.Sign < 0 ? "-" : "") + (lit.Type.Scale == 0 ? digits
                    : digits.Substring(0, digits.Length - lit.Type.Scale) + "." + digits.Substring(digits.Length - lit.Type.Scale));
                return "CAST('" + text + "' AS " + TypeName(lit.Type) + ")";
            }
            case TypeKind.Uuid:
            {
                string hex = Convert.ToHexString(lit.BytesValue!).ToLowerInvariant();
                return "CAST('" + hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20) + "' AS UUID)";
            }
            case TypeKind.IntervalYear:
                return "to_months(CAST(" + (lit.Years * 12 + lit.Months).ToString(inv) + " AS INTEGER))";
            case TypeKind.IntervalDay:
            {
                if (lit.SubsecondPrecision > 6 && lit.Subseconds % Pow10(lit.SubsecondPrecision - 6) != 0)
                    throw SubstraitTranslationException.Unsupported("an interval finer than microseconds");
                long sub = lit.SubsecondPrecision <= 6 ? lit.Subseconds * Pow10(6 - lit.SubsecondPrecision)
                    : lit.Subseconds / Pow10(lit.SubsecondPrecision - 6);
                long micros = checked(lit.Seconds * 1_000_000 + sub);
                return "(to_days(CAST(" + lit.Days.ToString(inv) + " AS INTEGER)) + to_microseconds(CAST(" + micros.ToString(inv) + " AS BIGINT)))";
            }
            default:
                throw SubstraitTranslationException.Unsupported($"a literal of type {lit.Type}");
        }
    }

    private static long Pow10(int exponent)
    {
        if (exponent < 0 || exponent > 18)
            throw SubstraitTranslationException.Unsupported($"precision step {exponent}");
        long result = 1;
        for (int i = 0; i < exponent; i++)
            result *= 10;
        return result;
    }
}
