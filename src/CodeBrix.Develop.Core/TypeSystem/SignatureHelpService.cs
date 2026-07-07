//
// SignatureHelpService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (Roslyn's own SignatureHelpService is internal; this is an original
//      minimal reimplementation over the public SemanticModel API)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeBrix.Develop.Core.TypeSystem;

/// <summary>
/// One overload shown in the signature-help popover, pre-split so the UI
/// can emphasize the active parameter.
/// </summary>
public class SignatureItem
{
    /// <summary>Everything before the parameter list ("string Path.Combine(").</summary>
    public string Prefix { get; set; }

    /// <summary>The rendered parameters, joined by ", " for display.</summary>
    public IReadOnlyList<string> Parameters { get; set; }

    /// <summary>Everything after the parameter list (")").</summary>
    public string Suffix { get; set; }
}

/// <summary>The result of a signature-help request.</summary>
public class SignatureHelpResult
{
    /// <summary>The candidate overloads.</summary>
    public IReadOnlyList<SignatureItem> Signatures { get; set; }

    /// <summary>Index into <see cref="Signatures"/> of the best overload.</summary>
    public int ActiveSignature { get; set; }

    /// <summary>Index of the parameter the caret is on.</summary>
    public int ActiveParameter { get; set; }

    /// <summary>UTF-16 span start of the argument list (for dismissal tracking).</summary>
    public int SpanStart { get; set; }

    /// <summary>UTF-16 span end of the argument list.</summary>
    public int SpanEnd { get; set; }
}

/// <summary>
/// Computes parameter hints ("signature help") for method invocations and
/// object creations at a caret position.
/// </summary>
public static class SignatureHelpService
{
    static readonly SymbolDisplayFormat parameterFormat = new SymbolDisplayFormat(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue);

    static readonly SymbolDisplayFormat returnTypeFormat = new SymbolDisplayFormat(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Computes signature help at the given UTF-16 offset, or null when the
    /// caret is not inside the argument list of an invocation or creation.
    /// </summary>
    public static SignatureHelpResult Compute(SemanticModel semanticModel, int offset, CancellationToken cancellationToken = default)
    {
        var root = semanticModel.SyntaxTree.GetRoot(cancellationToken);
        if (offset <= 0 || offset > root.FullSpan.End)
            return null;

        var token = root.FindToken(Math.Max(0, offset - 1));
        for (var node = token.Parent; node != null; node = node.Parent)
        {
            ArgumentListSyntax argumentList = null;
            IReadOnlyList<IMethodSymbol> candidates = null;
            ISymbol resolved = null;

            if (node is InvocationExpressionSyntax invocation && invocation.ArgumentList != null)
            {
                argumentList = invocation.ArgumentList;
                if (!IsInside(argumentList, offset))
                    continue;
                candidates = semanticModel.GetMemberGroup(invocation.Expression, cancellationToken)
                    .OfType<IMethodSymbol>().ToList();
                resolved = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol;
            }
            else if (node is BaseObjectCreationExpressionSyntax creation && creation.ArgumentList != null)
            {
                argumentList = creation.ArgumentList;
                if (!IsInside(argumentList, offset))
                    continue;
                var createdType = semanticModel.GetTypeInfo(creation, cancellationToken).Type as INamedTypeSymbol;
                if (createdType != null)
                {
                    candidates = createdType.InstanceConstructors
                        .Where(c => semanticModel.IsAccessible(offset, c))
                        .ToList();
                }
                resolved = semanticModel.GetSymbolInfo(creation, cancellationToken).Symbol;
            }
            else
            {
                continue;
            }

            if (candidates == null || candidates.Count == 0)
                return null;

            var activeParameter = argumentList.Arguments.GetSeparators().Count(s => s.SpanStart < offset);
            var activeSignature = 0;
            if (resolved is IMethodSymbol resolvedMethod)
            {
                var index = candidates.ToList().FindIndex(c =>
                    SymbolEqualityComparer.Default.Equals(c, resolvedMethod)
                    || SymbolEqualityComparer.Default.Equals(c, resolvedMethod.ReducedFrom)
                    || SymbolEqualityComparer.Default.Equals(c, resolvedMethod.OriginalDefinition));
                if (index >= 0)
                    activeSignature = index;
            }
            if (activeSignature == 0)
            {
                // Prefer an overload that has room for the parameter being typed.
                var index = candidates.ToList().FindIndex(c =>
                    c.Parameters.Length > activeParameter
                    || c.Parameters.Length > 0 && c.Parameters[c.Parameters.Length - 1].IsParams);
                if (index >= 0)
                    activeSignature = index;
            }

            var signatures = new List<SignatureItem>(candidates.Count);
            foreach (var method in candidates)
                signatures.Add(Render(method, semanticModel, offset));

            return new SignatureHelpResult
            {
                Signatures = signatures,
                ActiveSignature = activeSignature,
                ActiveParameter = activeParameter,
                SpanStart = argumentList.OpenParenToken.Span.End,
                SpanEnd = argumentList.CloseParenToken.IsMissing
                    ? root.FullSpan.End
                    : argumentList.CloseParenToken.SpanStart,
            };
        }
        return null;
    }

    // The caret counts as inside from just after '(' to just before ')' —
    // or to the end of the file while the ')' is still missing.
    static bool IsInside(ArgumentListSyntax argumentList, int offset)
    {
        if (offset <= argumentList.OpenParenToken.Span.Start)
            return false;
        if (offset < argumentList.OpenParenToken.Span.End)
            return false;
        if (argumentList.CloseParenToken.IsMissing)
            return true;
        return offset <= argumentList.CloseParenToken.SpanStart;
    }

    static SignatureItem Render(IMethodSymbol method, SemanticModel semanticModel, int offset)
    {
        string prefix;
        if (method.MethodKind == MethodKind.Constructor)
        {
            prefix = method.ContainingType.ToMinimalDisplayString(semanticModel, offset, returnTypeFormat) + "(";
        }
        else
        {
            var returnType = method.ReturnsVoid
                ? "void"
                : method.ReturnType.ToMinimalDisplayString(semanticModel, offset, returnTypeFormat);
            var container = method.ContainingType == null
                ? ""
                : method.ContainingType.ToMinimalDisplayString(semanticModel, offset, returnTypeFormat) + ".";
            var typeArguments = method.IsGenericMethod
                ? "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">"
                : "";
            prefix = $"{returnType} {container}{method.Name}{typeArguments}(";
        }

        var parameters = new List<string>(method.Parameters.Length);
        foreach (var parameter in method.Parameters)
            parameters.Add(parameter.ToMinimalDisplayString(semanticModel, offset, parameterFormat));

        return new SignatureItem { Prefix = prefix, Parameters = parameters, Suffix = ")" };
    }
}
