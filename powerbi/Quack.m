// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

[ Version = "1.0.0" ]
section Quack;

Driver = [
    Name = "QuackAdbc",
    Folder = "Quack",
    File = "quack_adbc.dll",
    DriverType = "Unmanaged",
    EntryPoint = "QuackAdbcDriverInit"
];

EscapeIdentifier = (identifier as text) as text => """" & Text.Replace(identifier, """", """""") & """";

EscapeStringLiteral = (value as text) as text =>
    "'" & Text.Replace(value, "'", "''") & "'";

AddConnectionStringOption = (options as record, name as text, value as any, optional metadata) as record =>
    if value = null then
        options
    else
        Record.AddField(options, name, value meta (metadata ?? []));

ValidateOptions = (options) as record =>
    let
        ValidOptionsMap = #table({"Name", "Type", "Description", "Default", "Validate", "Hidden"},
            {
                {"CommandTimeout", type nullable duration, Extension.LoadString("ValidPositiveDurationValue"), null, each _ = null or _ > #duration(0, 0, 0, 0), false},
                {"ReconnectOnSessionLoss", type nullable logical, Extension.LoadString("ValidLogicalValue"), false, each _ = null or _ is logical, false}
            }),
        ValidatedOptions = GetValidatedOptions(options, ValidOptionsMap)
    in
        ValidatedOptions;

GetValidatedOptions = (options, ValidOptionsMap) =>
    let
        VisibleKeys = Table.SelectRows(ValidOptionsMap, each not [Hidden])[Name],
        ValidKeys = Table.Column(ValidOptionsMap, "Name"),
        InvalidKeys = List.Difference(Record.FieldNames(options), ValidKeys),
        InvalidKeysText = if List.IsEmpty(InvalidKeys) then null else Text.Format(Extension.LoadString("InvalidOptionsKey"), {Text.Combine(InvalidKeys, ", "), Text.Combine(VisibleKeys, ", ")}),
        ValidateValue = (name, optionType, description, default, validate, value) =>
            if (value is null and (Type.IsNullable(optionType) or default <> null))
                or (Type.Is(Value.Type(value), optionType) and validate(value)) then null
            else Text.Format(Extension.LoadString("InvalidOptionsValue"), {name, value, description}),
        InvalidValues = List.RemoveNulls(Table.TransformRows(ValidOptionsMap,
            each ValidateValue([Name], [Type], [Description], [Default], [Validate], Record.FieldOrDefault(options, [Name], [Default])))),
        DefaultOptions = Record.FromTable(Table.RenameColumns(Table.SelectColumns(ValidOptionsMap, {"Name", "Default"}), {"Default", "Value"})),
        NullNotAllowedFields = List.RemoveNulls(Table.TransformRows(ValidOptionsMap,
            each if not Type.IsNullable([Type]) and null = Record.FieldOrDefault(options, [Name], [Default]) then [Name] else null)),
        NormalizedOptions = DefaultOptions & Record.RemoveFields(options, NullNotAllowedFields, MissingField.Ignore),
        Result = if null = options then DefaultOptions
                 else if not List.IsEmpty(InvalidKeys) then
                     error Error.Record("Expression.Error", InvalidKeysText)
                 else if not List.IsEmpty(InvalidValues) then
                     error Error.Record("Expression.Error", Text.Combine(InvalidValues, ", "))
                 else NormalizedOptions
    in
        Result;

[DataSource.Kind = "Quack", Publish = "Quack.Publish"]
shared Quack.Database = Value.ReplaceType(Quack.Function, Quack.Type);

Quack.Function = (uri as text, optional options as nullable record) as table =>
    let
        validatedOptions = ValidateOptions(options),
        result = QuackAdbcConnection(uri, validatedOptions)
    in
        result;

QuackAdbcConnection = (uri as text, options as nullable record) =>
    let
        // ADBC parameter key matches QuackAdbcDriver.CommandTimeoutSecondsParameter.
        // en-US culture forces a "." decimal separator so the C# parser (which
        // uses InvariantCulture) accepts fractional values from any locale.
        CommandTimeoutSeconds =
            if options[CommandTimeout]? <> null
                then Number.ToText(Duration.TotalSeconds(options[CommandTimeout]), "G", "en-US")
                else null,
        // Default false: server-side session state (transactions, SET, temp
        // tables) survives within a session and would be silently dropped on
        // a transparent reconnect. Users who know their workload is stateless
        // can opt in via the ReconnectOnSessionLoss option.
        ReconnectOnSessionLoss = if (options[ReconnectOnSessionLoss] ?? false) then "true" else "false",
        BaseOptions = [
            uri = uri,
            token = Extension.CurrentCredential()[Key],
            reconnect_on_session_loss = ReconnectOnSessionLoss
        ],
        ConnectionString = AddConnectionStringOption(
            BaseOptions,
            "command_timeout_seconds",
            CommandTimeoutSeconds),
        Connection = Adbc.Connection(Driver, ConnectionString, [], [ConnectionPoolType = 2]),
        ExecuteQueryCtor = (cxn) => (sql, optional opts) =>
            let
                connectionProps = if opts[Catalog]? <> null then [adbc.connection.catalog = opts[Catalog]] else [],
                queryOptions = [
                    ConnectionProperties = connectionProps,
                    IsMetadata = opts[IsMetadata]?
                ]
            in
                cxn[ExecuteQuery](sql, null, queryOptions),
        GetData = (query as text, resultType as type, ctx) => CreateDataTable(query, resultType, ctx, context[ExecuteQuery]),
        UniqueIdentifier = MakeUniqueIdentifier(ConnectionString),
        SqlGenerator = SqlView.Generator(UniqueIdentifier, QuackSqlGenerator, GetData),
        context = [
            Connection = Connection,
            ExecuteQuery = ExecuteQueryCtor(Connection),
            ExecuteQueryCtor = ExecuteQueryCtor,
            SqlGenerator = SqlGenerator
        ],
        Databases = GetDatabases(context)
    in
        Databases;

CreateDataTable = (query as text, resultType as type, context, executeQuery) =>
    Table.View(null, [
        GetType = () => resultType,
        GetRows = () =>
            let
                data = executeQuery(query, context[[Catalog],[IsMetadata]]?),
                oldNames = Table.ColumnNames(data),
                newNames = Table.ColumnNames(#table(resultType, {})),
                renamed = Table.RenameColumns(data, List.Zip({oldNames, newNames}))
            in
                renamed
    ]);

MakeNavTableType = (isLeaf) =>
    let
        dataType = type table meta [
            NavigationTable.ItemKind = "Table",
            Preview.Delay = "Table",
            NavigationTable.RowConfigurationColumn = "Kind"
        ],
        tableType = type table [
            Name = text,
            Description = nullable text,
            Data = dataType,
            Kind = text
        ],
        withKeys = Type.ReplaceTableKeys(tableType, {[Columns={"Name", "Kind"}, Primary=true]}) meta [
            NavigationTable.NameColumn="Name",
            NavigationTable.DataColumn="Data",
            NavigationTable.KindColumn="Kind"
        ]
    in
        withKeys;

GetDatabases = (context) =>
    let
        isLeaf = false,
        kind = "Database",
        command = "USE system.main; SELECT DISTINCT catalog_name as name FROM system.information_schema.schemata",
        tables = context[ExecuteQuery](command, [IsMetadata = true]),
        getSchemas = (name) => GetSchemas(context, name),
        withData = Table.AddColumn(tables, "Data", each getSchemas([name]), type table),
        withDescription = Table.AddColumn(withData, "Description", each null, type nullable text),
        withKind = Table.AddColumn(withDescription, "Kind", each kind meta [NavigationTable.IsLeaf = isLeaf], type text),
        selected = Table.SelectColumns(withKind, {"name", "Description", "Data", "Kind"}),
        renamed = Table.RenameColumns(selected, {{"name", "Name"}}),
        withFolding = Table.View(null, [
            GetType = () => MakeNavTableType(isLeaf),
            GetRows = () => renamed,
            OnSelectRows = (selector) => FoldNavigationStep(selector, getSchemas, kind),
            ThrowFoldingFailures = false
        ])
    in
        withFolding;

GetSchemas = (context, catalog) =>
    let
        isLeaf = false,
        kind = "Schema",
        command = "USE system.main; SELECT schema_name as name FROM system.information_schema.schemata WHERE catalog_name = " & EscapeStringLiteral(catalog)
            & " AND schema_name NOT IN ('information_schema', 'pg_catalog')",
        schemas = context[ExecuteQuery](command, [IsMetadata = true]),
        getTables = (name) => GetTables(context, catalog, name),
        withData = Table.AddColumn(schemas, "Data", each getTables([name]), type table),
        withDescription = Table.AddColumn(withData, "Description", each null, type nullable text),
        withKind = Table.AddColumn(withDescription, "Kind", each kind meta [NavigationTable.IsLeaf = isLeaf], type text),
        selected = Table.SelectColumns(withKind, {"name", "Description", "Data", "Kind"}),
        renamed = Table.RenameColumns(selected, {{"name", "Name"}}),
        withFolding = Table.View(null, [
            GetType = () => MakeNavTableType(isLeaf),
            GetRows = () => renamed,
            OnSelectRows = (selector) => FoldNavigationStep(selector, getTables, kind),
            ThrowFoldingFailures = false
        ])
    in
        withFolding;

GetTables = (context, catalog, schema) =>
    let
        isLeaf = true,
        kind = {"Table","View"},
        command = "USE system.main; SELECT table_name as name, table_type FROM system.information_schema.tables WHERE table_catalog = " & EscapeStringLiteral(catalog)
            & " AND table_schema = " & EscapeStringLiteral(schema),
        tables = context[ExecuteQuery](command, [IsMetadata = true]),
        getTable = (name) => GetTable(context, catalog, schema, name),
        withData = Table.AddColumn(tables, "Data", each getTable([name]), type table),
        withDescription = Table.AddColumn(withData, "Description", each null, type nullable text),
        withKind = Table.AddColumn(withDescription, "Kind",
            each (if [table_type] = "VIEW" then "View" else "Table") meta [NavigationTable.IsLeaf = isLeaf], type text),
        selected = Table.SelectColumns(withKind, {"name", "Description", "Data", "Kind"}),
        renamed = Table.RenameColumns(selected, {{"name", "Name"}}),
        withFolding = Table.View(null, [
            GetType = () => MakeNavTableType(isLeaf),
            GetRows = () => renamed,
            OnSelectRows = (selector) => FoldNavigationStep(selector, getTable, kind, true),
            ThrowFoldingFailures = false
        ])
    in
        withFolding;

GetTable = (context, catalog, schema, table) =>
    let
        tableType = GetTableType(context[ExecuteQuery], catalog, schema, table),
        tableReference = [Kind = "FromTable", Table = [Catalog = catalog, Schema = schema, Name = table]],
        withSqlView = context[SqlGenerator](tableReference, tableType, [])
    in
        withSqlView;

FoldNavigationStep = (selector, loader, kind, optional immediate) =>
    let
        reduceAnd = (ast) => if ast[Kind] = "Binary" and ast[Operator] = "And" then List.Combine({@reduceAnd(ast[Left]), @reduceAnd(ast[Right])}) else {ast},
        matchFieldAccess = (ast) => if ast[Kind] = "FieldAccess" and ast[Expression] = RowExpression.Row then ast[MemberName] else ...,
        matchConstant = (ast) => if ast[Kind] = "Constant" then ast[Value] else ...,
        matchIndex = (ast) => if ast[Kind] = "Binary" and ast[Operator] = "Equals"
            then
                if ast[Left][Kind] = "FieldAccess"
                    then Record.AddField([], matchFieldAccess(ast[Left]), matchConstant(ast[Right]))
                    else Record.AddField([], matchFieldAccess(ast[Right]), matchConstant(ast[Left]))
            else ...,
        predicate1 = Record.Combine(List.Transform(reduceAnd(RowExpression.From(selector)), matchIndex)),
        isKindList = kind is list,
        kindMatch = if isKindList then List.Contains(kind,predicate1[Kind]?) else predicate1[Kind]? = kind,
        predicate2 = if kindMatch then Record.RemoveFields(predicate1, {"Kind"}) else predicate1,
        pickKind = if isKindList then 
                       if List.Contains(kind,predicate1[Kind]?) then predicate1[Kind]? else ...
                   else kind,
        name = if Record.FieldCount(predicate2) = 1 and predicate2[Name]? <> null then predicate2[Name] else ...,

        // TODO: Make Description work when folding

        dataResult = loader(name),
        emptyResult = #table(type table [Name = text, Description = nullable text, Data = table, Kind = text], {}),
        resultOrEmpty = if immediate = true
            // TODO: Adjust this for error shape
            then try dataResult catch (e) => if Text.Contains(e[Message], "(42S02)") then null else error e
            else dataResult
    in
        if resultOrEmpty = null
            then emptyResult
            else Table.FromRecords({[Name=name, Description="", Data=resultOrEmpty, Kind=pickKind]});

GetTableType = (exec, catalog, schema, table) =>
    let
        command = "USE system.main; SELECT column_name, data_type, is_nullable, numeric_precision, numeric_scale, character_maximum_length "
            & "FROM system.information_schema.columns "
            & "WHERE table_catalog = " & EscapeStringLiteral(catalog)
            & " AND table_schema = " & EscapeStringLiteral(schema)
            & " AND table_name = " & EscapeStringLiteral(table)
            & " ORDER BY ordinal_position",
        columnInfo = Table.Buffer(exec(command, [IsMetadata = true])),
        columnNames = columnInfo[column_name],
        columnTypes = List.Transform(Table.ToRecords(columnInfo), each [
            Type = GetColumnType([data_type], [is_nullable], [numeric_precision], [numeric_scale], [character_maximum_length]),
            Optional = false
        ]),
        rowType = Type.ForRecord(Record.FromList(columnTypes, columnNames), false),
        primaryKeys = GetPrimaryKeys(exec, catalog, schema, table),
        tableType = type table rowType,
        tableTypeWithPrimaryKey = Type.ReplaceTableKeys(tableType, primaryKeys)
    in
        tableTypeWithPrimaryKey;

// Parse the base type from potentially parameterized data_type values like "DECIMAL(18,2)" or "INTEGER[]"
ParseBaseType = (dataType as text) as text =>
    let
        parenPos = Text.PositionOf(dataType, "("),
        bracketPos = Text.PositionOf(dataType, "["),
        endPos = List.Min(List.RemoveNulls({
            if parenPos >= 0 then parenPos else null,
            if bracketPos >= 0 then bracketPos else null,
            Text.Length(dataType)
        })),
        baseType = Text.Upper(Text.Trim(Text.Start(dataType, endPos)))
    in
        baseType;

AdjustType = (nullable as logical, mtype as type, nativeTypeName as text) =>
    let
        withNullable = if nullable then type nullable mtype else mtype,
        withFacets = Type.ReplaceFacets(withNullable, [NativeTypeName = nativeTypeName])
    in
        withFacets;

ColumnTypeMap = [
    BOOLEAN = type logical,
    TINYINT = Int32.Type,
    SMALLINT = Int32.Type,
    INTEGER = Int32.Type,
    BIGINT = Int64.Type,
    HUGEINT = Decimal.Type,
    UTINYINT = Int32.Type,
    USMALLINT = Int32.Type,
    UINTEGER = Int64.Type,
    UBIGINT = Decimal.Type,
    FLOAT = Double.Type,
    DOUBLE = Double.Type,
    DECIMAL = Decimal.Type,
    VARCHAR = type text,
    BLOB = type binary,
    DATE = type date,
    TIME = type time,
    TIMESTAMP = type datetime,
    #"TIMESTAMP WITH TIME ZONE" = type datetimezone,
    TIMESTAMPTZ = type datetimezone,
    TIMESTAMP_S = type datetime,
    TIMESTAMP_MS = type datetime,
    TIMESTAMP_NS = type datetime,
    INTERVAL = type text,
    UUID = type text,
    JSON = type text,
    BIT = type text,
    ENUM = type text,
    LIST = type text,
    STRUCT = type text,
    MAP = type text,
    UNION = type text,
    ARRAY = type text
];

GetColumnType = (dataType as text, isNullableText as text, numericPrecision, numericScale, charMaxLength) =>
    let
        baseType = ParseBaseType(dataType),
        isNullable = isNullableText = "YES",
        mtype = Record.FieldOrDefault(ColumnTypeMap, baseType, type text),
        adjustedType = if baseType = "DECIMAL" and numericPrecision <> null then
            Type.ReplaceFacets(
                AdjustType(isNullable, Decimal.Type, "DECIMAL"),
                [NativeTypeName = "DECIMAL", NumericPrecisionBase = 10, NumericPrecision = numericPrecision, NumericScale = numericScale ?? 0])
        else if baseType = "VARCHAR" and charMaxLength <> null then
            Type.ReplaceFacets(
                AdjustType(isNullable, type text, "VARCHAR"),
                [NativeTypeName = "VARCHAR", MaxLength = charMaxLength, IsVariableLength = true])
        else
            AdjustType(isNullable, mtype, baseType)
    in
        adjustedType;

GetPrimaryKeys = (exec, catalog, schema, table) =>
    let
        command = "USE system.main; SELECT column_name FROM system.information_schema.table_constraints tc "
            & "JOIN system.information_schema.key_column_usage kcu "
            & "ON tc.constraint_name = kcu.constraint_name "
            & "AND tc.table_catalog = kcu.table_catalog "
            & "AND tc.table_schema = kcu.table_schema "
            & "AND tc.table_name = kcu.table_name "
            & "WHERE tc.constraint_type = 'PRIMARY KEY' "
            & "AND tc.table_catalog = " & EscapeStringLiteral(catalog) & " "
            & "AND tc.table_schema = " & EscapeStringLiteral(schema) & " "
            & "AND tc.table_name = " & EscapeStringLiteral(table) & " "
            & "ORDER BY kcu.ordinal_position",
        result = try exec(command, [IsMetadata = true]) otherwise #table({"column_name"}, {}),
        primaryKeyColumns = result[column_name]
    in
        if List.IsEmpty(primaryKeyColumns) then {} else {[Columns = primaryKeyColumns, Primary = true]};

ModuleIdentifier = () => ...;
MakeUniqueIdentifier = (connectionString) => [Module = ModuleIdentifier, Signature = connectionString];

// Type for the exported function
Quack.Type =
    let
        CommandTimeoutType = type nullable duration meta [
            Documentation.FieldCaption = Extension.LoadString("CommandTimeoutCaption"),
            Documentation.SampleValues = { #duration(0, 0, 5, 0) }
        ],
        ReconnectOnSessionLossType = type nullable logical meta [
            Documentation.FieldCaption = Extension.LoadString("ReconnectOnSessionLossCaption"),
            Documentation.SampleValues = { false }
        ],
        FunctionType = Type.ForFunction([
            Parameters = [
                uri = type text meta [
                    Documentation.FieldCaption = Extension.LoadString("DatabaseParameterCaption"),
                    Documentation.SampleValues = { "quack:127.0.0.1:9494" }
                ],
                options = type [
                    optional CommandTimeout = CommandTimeoutType,
                    optional ReconnectOnSessionLoss = ReconnectOnSessionLossType
                ] meta [
                    Documentation.FieldCaption = Extension.LoadString("OptionsParameterCaption")
                ]
            ],
            ReturnType = type table
        ], 1),
        AddMetadata = Value.ReplaceMetadata(
            FunctionType,
            [
                Documentation.Name = "Quack",
                Documentation.Caption = Extension.LoadString("FormulaTitle"),
                Documentation.Description = Extension.LoadString("Quack_Description"),
                Documentation.LongDescription = Extension.LoadString("Quack_LongDescription")
            ])
    in
        AddMetadata;

// DataSource.Kind definition
Quack = [
    Description = "Quack",
    Type = "Custom",
    MakeResourcePath = (uri) => uri,
    ParseResourcePath = (resourcePath) => {resourcePath},
    TestConnection = (resourcePath) => { "Quack.Database" } & {resourcePath},
    Authentication = [
        Key = []
    ]
];

Quack.Publish = [
    ButtonText = { Extension.LoadString("ButtonTitle"), Extension.LoadString("ButtonHelp") },
    Category = "Database",
    SupportsDirectQuery = true
];

// Extension library functions
Extension.LoadExpression = (name as text) =>
    let
        binary = Extension.Contents(name),
        asText = Text.FromBinary(binary)
    in
        Expression.Evaluate(asText, #shared);

QuackSqlGenerator = Extension.LoadExpression("SqlGenerator.pqm");

Adbc.Connection = try #shared[Adbc.Connection] otherwise (driver, databaseProperties, connectionProperties, options) =>
    error Error.Record("Expression.Error", "The Adbc.Connection function is not available in this environment");
SqlView.Generator = try #shared[SqlView.Generator] otherwise (uniqueIdentifier, sqlGenerator, getData) =>
    error Error.Record("Expression.Error", "The SqlView.Generator function is not available in this environment");
