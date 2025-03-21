﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.UsePatternMatching;

/// <summary>
/// Looks for code of the forms:
/// <code>
///     var x = o as Type;
///     if (x != null) ...
/// </code>
/// and converts it to:
/// <code>
///     if (o is Type x) ...
/// </code>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed partial class CSharpAsAndNullCheckDiagnosticAnalyzer()
    : AbstractBuiltInCodeStyleDiagnosticAnalyzer(
        IDEDiagnosticIds.InlineAsTypeCheckId,
        EnforceOnBuildValues.InlineAsType,
        CSharpCodeStyleOptions.PreferPatternMatchingOverAsWithNullCheck,
        new LocalizableResourceString(
            nameof(CSharpAnalyzersResources.Use_pattern_matching), CSharpAnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources)))
{
    public override DiagnosticAnalyzerCategory GetAnalyzerCategory()
        => DiagnosticAnalyzerCategory.SemanticSpanAnalysis;

    protected override void InitializeWorker(AnalysisContext context)
        // We wrap the SyntaxNodeAction within a CodeBlockStartAction, which allows us to
        // get callbacks for expression nodes, but analyze nodes across the entire code block
        // and eventually report a diagnostic on the local declaration statement node.
        // Without the containing CodeBlockStartAction, our reported diagnostic would be classified
        // as a non-local diagnostic and would not participate in lightbulb for computing code fixes.
        => context.RegisterCodeBlockStartAction<SyntaxKind>(blockStartContext =>
        blockStartContext.RegisterSyntaxNodeAction(SyntaxNodeAction,
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression,
            SyntaxKind.IsExpression,
            SyntaxKind.IsPatternExpression));

    private void SyntaxNodeAction(SyntaxNodeAnalysisContext syntaxContext)
    {
        var node = syntaxContext.Node;
        var syntaxTree = node.SyntaxTree;

        // "x is Type y" is only available in C# 7.0 and above. Don't offer this refactoring
        // in projects targeting a lesser version.
        if (syntaxTree.Options.LanguageVersion() < LanguageVersion.CSharp7)
            return;

        var styleOption = syntaxContext.GetCSharpAnalyzerOptions().PreferPatternMatchingOverAsWithNullCheck;
        if (!styleOption.Value || ShouldSkipAnalysis(syntaxContext, styleOption.Notification))
        {
            // Bail immediately if the user has disabled this feature.
            return;
        }

        var comparison = (ExpressionSyntax)node;
        var (comparisonLeft, comparisonRight) = comparison switch
        {
            BinaryExpressionSyntax binaryExpression => (binaryExpression.Left, (SyntaxNode)binaryExpression.Right),
            IsPatternExpressionSyntax isPattern => (isPattern.Expression, isPattern.Pattern),
            _ => throw ExceptionUtilities.Unreachable(),
        };

        var operand = GetNullCheckOperand(comparisonLeft, comparison.Kind(), comparisonRight)?.WalkDownParentheses();
        if (operand == null)
            return;

        var semanticModel = syntaxContext.SemanticModel;
        if (operand is CastExpressionSyntax castExpression)
        {
            // Unwrap object cast
            var castType = semanticModel.GetTypeInfo(castExpression.Type).Type;
            if (castType?.SpecialType == SpecialType.System_Object)
                operand = castExpression.Expression;
        }

        var cancellationToken = syntaxContext.CancellationToken;
        if (semanticModel.GetSymbolInfo(comparison, cancellationToken).GetAnySymbol().IsUserDefinedOperator())
            return;

        if (!TryGetTypeCheckParts(semanticModel, operand,
                out var declarator,
                out var asExpression,
                out var localSymbol))
        {
            return;
        }

        if (declarator is not { Parent.Parent: LocalDeclarationStatementSyntax localStatement })
            return;

        // Don't convert if the as is part of a local declaration with a using keyword
        // eg using var x = y as MyObject;
        if (localStatement is LocalDeclarationStatementSyntax localDecl && localDecl.UsingKeyword != default)
            return;

        var enclosingBlock = localStatement.Parent;
        if (enclosingBlock is not BlockSyntax and not SwitchSectionSyntax)
            return;

        // Bail out if the potential diagnostic location is outside the analysis span.
        if (!syntaxContext.ShouldAnalyzeSpan(localStatement.Span))
            return;

        var typeNode = asExpression.Right;
        var asType = semanticModel.GetTypeInfo(typeNode, cancellationToken).Type;
        if (asType.IsNullable())
        {
            // Not legal to write "x is int? y"
            return;
        }

        if (asType?.TypeKind == TypeKind.Dynamic)
        {
            // Not legal to use dynamic in a pattern.
            return;
        }

        if (!localSymbol.Type.Equals(asType))
        {
            // We have something like:
            //
            //      BaseType b = x as DerivedType;
            //      if (b != null) { ... }
            //
            // It's not necessarily safe to convert this to:
            //
            //      if (x is DerivedType b) { ... }
            //
            // That's because there may be later code that wants to do something like assign a
            // 'BaseType' into 'b'.  As we've now claimed that it must be DerivedType, that
            // won't work.  This might also cause unintended changes like changing overload
            // resolution.  So, we conservatively do not offer the change in a situation like this.
            return;
        }

        // Check if the as operand is ever written up to the point of null check.
        //
        //      var s = field as string;
        //      field = null;
        //      if (s != null) { ... }
        //
        // It's no longer safe to use pattern-matching because 'field is string s' would never be true.
        // 
        // Additionally, also bail out if the assigned local is referenced (i.e. read/write/nameof) up to the point of null check.
        //      var s = field as string;
        //      MethodCall(flag: s == null);
        //      if (s != null) { ... }
        //
        var asOperand = semanticModel.GetSymbolInfo(asExpression.Left, cancellationToken).Symbol;
        var localStatementStart = localStatement.SpanStart;
        var comparisonSpanStart = comparison.SpanStart;

        foreach (var descendentNode in enclosingBlock.DescendantNodes())
        {
            var descendentNodeSpanStart = descendentNode.SpanStart;
            if (descendentNodeSpanStart <= localStatementStart)
                continue;

            if (descendentNodeSpanStart >= comparisonSpanStart)
                break;

            if (descendentNode is IdentifierNameSyntax identifierName)
            {
                // Check if this is a 'write' to the asOperand.
                if (identifierName.Identifier.ValueText == asOperand?.Name &&
                    asOperand.Equals(semanticModel.GetSymbolInfo(identifierName, cancellationToken).Symbol) &&
                    identifierName.IsWrittenTo(semanticModel, cancellationToken))
                {
                    return;
                }

                // Check is a reference of any sort (i.e. read/write/nameof) to the local.
                if (identifierName.Identifier.ValueText == localSymbol.Name)
                    return;
            }
        }

        if (!Analyzer.CanSafelyConvertToPatternMatching(
                semanticModel, localSymbol, comparison, operand,
                localStatement, declarator, enclosingBlock, cancellationToken))
        {
            return;
        }

        var comparisonEnclosingBlock = comparison.AncestorsAndSelf().FirstOrDefault(n => n is BlockSyntax);
        if (comparisonEnclosingBlock is null)
            return;

        if (comparisonEnclosingBlock != enclosingBlock)
        {
            // ok, the local variable is defined in a different block than the block that the `x != null` is in. If
            // we then update the `x != null` to `o is X x` we may break scoping if the variable is referenced
            // before/after that scope.
            foreach (var descendentNode in enclosingBlock.DescendantNodes())
            {
                var descendentNodeSpanStart = descendentNode.SpanStart;
                if (descendentNodeSpanStart <= localStatementStart)
                    continue;

                if (descendentNode is IdentifierNameSyntax identifierName && !
                    descendentNode.Span.IntersectsWith(comparisonEnclosingBlock.Span) &&
                    identifierName.Identifier.ValueText == localSymbol.Name &&
                    localSymbol.Equals(semanticModel.GetSymbolInfo(identifierName, cancellationToken).Symbol))
                {
                    return;
                }
            }
        }

        // If we have an annotated local (like `string? s = o as string`) we can't convert this to `o is string s`
        // if there are any assignments to `s` that end up assigning a `string?`.  These will now give a nullable
        // warning.
        if (localSymbol.Type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            foreach (var descendentNode in enclosingBlock.DescendantNodes())
            {
                var descendentNodeSpanStart = descendentNode.SpanStart;
                if (descendentNodeSpanStart <= localStatementStart)
                    continue;

                if (descendentNode is IdentifierNameSyntax identifierName &&
                    identifierName.Identifier.ValueText == localSymbol.Name &&
                    localSymbol.Equals(semanticModel.GetSymbolInfo(identifierName, cancellationToken).Symbol))
                {
                    if (identifierName.Parent is AssignmentExpressionSyntax assignmentExpression &&
                        assignmentExpression.Left == identifierName)
                    {
                        var rightType = semanticModel.GetTypeInfo(assignmentExpression.Right);
                        if (rightType.Type is null or { NullableAnnotation: NullableAnnotation.Annotated })
                            return;
                    }
                }
            }
        }

        // Looks good!
        var additionalLocations = ImmutableArray.Create(
            declarator.GetLocation(),
            comparison.GetLocation(),
            asExpression.GetLocation());

        // Put a diagnostic with the appropriate severity on the declaration-statement itself.
        syntaxContext.ReportDiagnostic(DiagnosticHelper.Create(
            Descriptor,
            localStatement.GetLocation(),
            styleOption.Notification,
            syntaxContext.Options,
            additionalLocations,
            properties: null));
    }

    private static bool TryGetTypeCheckParts(
        SemanticModel semanticModel,
        SyntaxNode operand,
        [NotNullWhen(true)] out VariableDeclaratorSyntax? declarator,
        [NotNullWhen(true)] out BinaryExpressionSyntax? asExpression,
        [NotNullWhen(true)] out ILocalSymbol? localSymbol)
    {
        switch (operand.Kind())
        {
            case SyntaxKind.IdentifierName:
                {
                    // var x = e as T;
                    // if (x != null) F(x);
                    var identifier = (IdentifierNameSyntax)operand;
                    if (!TryFindVariableDeclarator(semanticModel, identifier, out localSymbol, out declarator))
                        break;

                    var initializerValue = declarator.Initializer?.Value;
                    if (!initializerValue.IsKind(SyntaxKind.AsExpression, out asExpression))
                        break;

                    return true;
                }

            case SyntaxKind.SimpleAssignmentExpression:
                {
                    // T x;
                    // if ((x = e as T) != null) F(x);
                    var assignment = (AssignmentExpressionSyntax)operand;
                    if (!assignment.Right.IsKind(SyntaxKind.AsExpression, out asExpression) ||
                        assignment.Left is not IdentifierNameSyntax identifier)
                    {
                        break;
                    }

                    if (!TryFindVariableDeclarator(semanticModel, identifier, out localSymbol, out declarator))
                        break;

                    return true;
                }
        }

        declarator = null;
        asExpression = null;
        localSymbol = null;
        return false;
    }

    private static bool TryFindVariableDeclarator(
        SemanticModel semanticModel,
        IdentifierNameSyntax identifier,
        [NotNullWhen(true)] out ILocalSymbol? localSymbol,
        [NotNullWhen(true)] out VariableDeclaratorSyntax? declarator)
    {
        localSymbol = semanticModel.GetSymbolInfo(identifier).Symbol as ILocalSymbol;
        declarator = localSymbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
        return localSymbol != null && declarator != null;
    }

    private static ExpressionSyntax? GetNullCheckOperand(ExpressionSyntax left, SyntaxKind comparisonKind, SyntaxNode right)
    {
        if (left.IsKind(SyntaxKind.NullLiteralExpression))
        {
            // null == x
            // null != x
            return (ExpressionSyntax)right;
        }

        if (right.IsKind(SyntaxKind.NullLiteralExpression))
        {
            // x == null
            // x != null
            return left;
        }

        if (right is PredefinedTypeSyntax predefinedType
            && predefinedType.Keyword.IsKind(SyntaxKind.ObjectKeyword)
            && comparisonKind == SyntaxKind.IsExpression)
        {
            // x is object
            return left;
        }

        if (right is ConstantPatternSyntax constantPattern
            && constantPattern.Expression.IsKind(SyntaxKind.NullLiteralExpression)
            && comparisonKind == SyntaxKind.IsPatternExpression)
        {
            // x is null
            return left;
        }

        return null;
    }
}
