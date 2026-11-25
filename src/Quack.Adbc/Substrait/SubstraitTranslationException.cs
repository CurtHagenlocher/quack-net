// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
//
// Derived from kusto_adbc (src/KustoAdbc/Substrait/SubstraitTranslationException.cs);
// see THIRD-PARTY-NOTICES.md. Changes: namespace, messages.

namespace Quack.Adbc.Substrait;

/// <summary>
/// Thrown when a Substrait plan cannot be translated to T-SQL, with a message
/// identifying the specific cause. Plans that are malformed or that use
/// something the driver cannot translate with the same semantics both raise
/// this exception.
/// </summary>
// [FABRICATOR-PATCH: OURS-BY-DESIGN] no ADBC here: a plain exception, which the pushdown reads as "decline"
public sealed class SubstraitTranslationException : Exception
{
    public SubstraitTranslationException(string message)
        : base(message)
    {
    }

    /// <summary>Malformed plan structure (missing required fields).</summary>
    internal static SubstraitTranslationException MalformedPlan(string detail)
        => new($"Malformed Substrait plan: {detail}");

    /// <summary>Plan references a function not declared in extensions.</summary>
    internal static SubstraitTranslationException UndeclaredFunction(int functionRef)
        => new($"Substrait plan references undeclared function (anchor={functionRef}). " +
               $"Ensure the plan includes a SimpleExtensionDeclaration for this function.");

    /// <summary>Plan declares a function that has no T-SQL equivalent.</summary>
    internal static SubstraitTranslationException UnsupportedFunction(string functionSignature)
        => new($"Unsupported Substrait function '{functionSignature}' has no T-SQL translation. " +
               $"The driver's Substrait dialect (SqlServerSubstrait.DialectYaml) lists the supported functions.");

    /// <summary>Plan uses a relation type we don't support.</summary>
    internal static SubstraitTranslationException UnsupportedRelation(int relFieldNumber)
        => new($"Unsupported Substrait relation type (Rel field {relFieldNumber}). " +
               $"Supported: read, filter, fetch, aggregate, sort, join, project, set, cross, top_n.");

    /// <summary>Plan uses a construct that has no faithful T-SQL translation.</summary>
    internal static SubstraitTranslationException Unsupported(string detail)
        => new($"Unsupported Substrait construct: {detail}");

    /// <summary>A function option was requested, but with no value the driver can deliver.</summary>
    internal static SubstraitTranslationException UnsupportedOption(string function, string option, IEnumerable<string> requested)
        => new($"Function '{function}' option '{option}' requests [{string.Join(", ", requested)}], " +
               $"none of which the SQL Server driver can deliver.");

    /// <summary>Expression type not supported.</summary>
    internal static SubstraitTranslationException UnsupportedExpression(string detail)
        => new($"Unsupported Substrait expression: {detail}");

    /// <summary>Plan contains a field that we cannot safely ignore during translation.</summary>
    internal static SubstraitTranslationException UnexpectedField(string messageName, int fieldNumber)
        => new($"Unexpected field {fieldNumber} in Substrait {messageName}. " +
               $"This field may affect query semantics and cannot be safely ignored.");

    /// <summary>Unsupported literal type.</summary>
    internal static SubstraitTranslationException UnsupportedLiteral(int fieldNumber)
        => new($"Unsupported Substrait literal type (Literal field {fieldNumber}).");
}
