﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CodeRefactorings.InvertIf
{
    internal abstract partial class AbstractInvertIfCodeRefactoringProvider
    {
        internal static class Negator
        {
            private const string LongLength = "LongLength";

            private static readonly Dictionary<BinaryOperatorKind, BinaryOperatorKind> s_negatedBinaryMap =
                new Dictionary<BinaryOperatorKind, BinaryOperatorKind>
                {
                    {BinaryOperatorKind.Equals, BinaryOperatorKind.NotEquals},
                    {BinaryOperatorKind.NotEquals, BinaryOperatorKind.Equals},
                    {BinaryOperatorKind.LessThan, BinaryOperatorKind.GreaterThanOrEqual},
                    {BinaryOperatorKind.GreaterThan, BinaryOperatorKind.LessThanOrEqual},
                    {BinaryOperatorKind.LessThanOrEqual, BinaryOperatorKind.GreaterThan},
                    {BinaryOperatorKind.GreaterThanOrEqual, BinaryOperatorKind.LessThan},
                    {BinaryOperatorKind.Or, BinaryOperatorKind.And},
                    {BinaryOperatorKind.And, BinaryOperatorKind.Or},
                    {BinaryOperatorKind.ConditionalOr, BinaryOperatorKind.ConditionalAnd},
                    {BinaryOperatorKind.ConditionalAnd, BinaryOperatorKind.ConditionalOr},
                };

            internal static SyntaxNode Negate(
                SyntaxNode expression,
                SyntaxGenerator generator,
                ISyntaxFactsService syntaxFacts,
                SemanticModel semanticModel,
                CancellationToken cancellationToken)
            {
                if (syntaxFacts.IsParenthesizedExpression(expression))
                {
                    return syntaxFacts.Parenthesize(
                        Negate(
                            syntaxFacts.GetExpressionOfParenthesizedExpression(expression),
                            generator,
                            syntaxFacts,
                            semanticModel,
                            cancellationToken))
                        .WithTriviaFrom(expression);
                }
                if (syntaxFacts.IsBinaryExpression(expression))
                {
                    return GetNegationOfBinaryExpression(expression, generator, syntaxFacts, semanticModel, cancellationToken);
                }
                else if (syntaxFacts.IsLiteralExpression(expression))
                {
                    return GetNegationOfLiteralExpression(expression, generator, semanticModel);
                }
                else if (syntaxFacts.IsLogicalNotExpression(expression))
                {
                    return GetNegationOfLogicalNotExpression(expression, syntaxFacts);
                }

                return generator.LogicalNotExpression(expression);
            }

            private static SyntaxNode GetNegationOfBinaryExpression(
                SyntaxNode expressionNode,
                SyntaxGenerator generator,
                ISyntaxFactsService syntaxFacts,
                SemanticModel semanticModel,
                CancellationToken cancellationToken)
            {
                syntaxFacts.GetPartsOfBinaryExpression(expressionNode, out var leftOperand, out var operatorToken, out var rightOperand);

                var operation = semanticModel.GetOperation(expressionNode, cancellationToken);
                if (operation.Kind == OperationKind.IsPattern)
                {
                    return generator.LogicalNotExpression(expressionNode);
                }

                var binaryOperation = (IBinaryOperation)operation;

                if (!s_negatedBinaryMap.TryGetValue(binaryOperation.OperatorKind, out var negatedKind))
                {
                    return generator.LogicalNotExpression(expressionNode);
                }
                else
                {
                    var negateOperands = false;
                    switch (binaryOperation.OperatorKind)
                    {
                        case BinaryOperatorKind.Or:
                        case BinaryOperatorKind.And:
                        case BinaryOperatorKind.ConditionalAnd:
                        case BinaryOperatorKind.ConditionalOr:
                            negateOperands = true;
                            break;
                    }

                    // Workaround for https://github.com/dotnet/roslyn/issues/23956
                    // Issue to remove this when above is merged
                    if (binaryOperation.OperatorKind == BinaryOperatorKind.Or && syntaxFacts.IsConditionalOr(expressionNode))
                    {
                        negatedKind = BinaryOperatorKind.ConditionalAnd;
                    }
                    else if (binaryOperation.OperatorKind == BinaryOperatorKind.And && syntaxFacts.IsConditionalAnd(expressionNode))
                    {
                        negatedKind = BinaryOperatorKind.ConditionalOr;
                    }

                    var newLeftOperand = leftOperand;
                    var newRightOperand = rightOperand;
                    if (negateOperands)
                    {
                        newLeftOperand = Negate(leftOperand, generator, syntaxFacts, semanticModel, cancellationToken);
                        newRightOperand = Negate(rightOperand, generator, syntaxFacts, semanticModel, cancellationToken);
                    }

                    var newBinaryExpressionSyntax = NewBinaryOperation(binaryOperation, newLeftOperand, negatedKind, newRightOperand, generator)
                        .WithTriviaFrom(expressionNode);

                    var newToken = syntaxFacts.GetOperatorTokenOfBinaryExpression(newBinaryExpressionSyntax);
                    var newTokenWithTrivia = newToken.WithTriviaFrom(operatorToken);
                    return newBinaryExpressionSyntax.ReplaceToken(newToken, newTokenWithTrivia);
                }
            }

            private static SyntaxNode NewBinaryOperation(
                IBinaryOperation binaryOperation,
                SyntaxNode leftOperand,
                BinaryOperatorKind operationKind,
                SyntaxNode rightOperand,
                SyntaxGenerator generator)
            {
                switch (operationKind)
                {
                    case BinaryOperatorKind.Equals:
                        return binaryOperation.LeftOperand.Type?.IsValueType == true && binaryOperation.RightOperand.Type?.IsValueType == true
                            ? generator.ValueEqualsExpression(leftOperand, rightOperand)
                            : generator.ReferenceEqualsExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.NotEquals:
                        return binaryOperation.LeftOperand.Type?.IsValueType == true && binaryOperation.RightOperand.Type?.IsValueType == true
                            ? generator.ValueNotEqualsExpression(leftOperand, rightOperand)
                            : generator.ReferenceNotEqualsExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.LessThanOrEqual:
                        return IsSpecialCaseBinaryExpression(binaryOperation, operationKind)
                            ? generator.ValueEqualsExpression(leftOperand, rightOperand)
                            : generator.LessThanOrEqualExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.GreaterThanOrEqual:
                        return IsSpecialCaseBinaryExpression(binaryOperation, operationKind)
                            ? generator.ValueEqualsExpression(leftOperand, rightOperand)
                            : generator.GreaterThanOrEqualExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.LessThan:
                        return generator.LessThanExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.GreaterThan:
                        return generator.GreaterThanExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.Or:
                        return generator.BitwiseOrExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.And:
                        return generator.BitwiseAndExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.ConditionalOr:
                        return generator.LogicalOrExpression(leftOperand, rightOperand);
                    case BinaryOperatorKind.ConditionalAnd:
                        return generator.LogicalAndExpression(leftOperand, rightOperand);
                }

                return null;
            }

            /// <summary>
            /// Returns true if the binaryExpression consists of an expression that can never be negative, 
            /// such as length or unsigned numeric types, being compared to zero with greater than, 
            /// less than, or equals relational operator.
            /// </summary>
            private static bool IsSpecialCaseBinaryExpression(
                IBinaryOperation binaryOperation,
                BinaryOperatorKind operationKind)
            {
                if (binaryOperation == null)
                {
                    return false;
                }

                var rightOperand = RemoveImplicitConversion(binaryOperation.RightOperand);
                var leftOperand = RemoveImplicitConversion(binaryOperation.LeftOperand);

                switch (operationKind)
                {
                    case BinaryOperatorKind.LessThanOrEqual when IsNumericLiteral(rightOperand):
                        return CanSimplifyToLengthEqualsZeroExpression(leftOperand, (ILiteralOperation)rightOperand);
                    case BinaryOperatorKind.GreaterThanOrEqual when IsNumericLiteral(leftOperand):
                        return CanSimplifyToLengthEqualsZeroExpression(rightOperand, (ILiteralOperation)leftOperand);
                }

                return false;
            }

            private static bool IsNumericLiteral(IOperation operation)
                => operation.Kind == OperationKind.Literal && operation.Type.IsNumericType();

            private static IOperation RemoveImplicitConversion(IOperation operation)
            {
                return operation is IConversionOperation conversion && conversion.IsImplicit
                    ? RemoveImplicitConversion(conversion.Operand)
                    : operation;
            }

            private static bool CanSimplifyToLengthEqualsZeroExpression(
                IOperation variableExpression,
                ILiteralOperation numericLiteralExpression)
            {
                var numericValue = numericLiteralExpression.ConstantValue;
                if (numericValue.HasValue && numericValue.Value is 0)
                {
                    if (variableExpression is IPropertyReferenceOperation propertyOperation)
                    {
                        var property = propertyOperation.Property;
                        if ((property.Name == nameof(Array.Length) || property.Name == LongLength))
                        {
                            var containingType = property.ContainingType;
                            if (containingType?.SpecialType == SpecialType.System_Array ||
                                containingType.SpecialType == SpecialType.System_String)
                            {
                                return true;
                            }
                        }
                    }

                    var type = variableExpression.Type;
                    if (type != null)
                    {
                        switch (type.SpecialType)
                        {
                            case SpecialType.System_Byte:
                            case SpecialType.System_UInt16:
                            case SpecialType.System_UInt32:
                            case SpecialType.System_UInt64:
                                return true;
                        }
                    }
                }

                return false;
            }

            private static SyntaxNode GetNegationOfLiteralExpression(
                SyntaxNode expression,
                SyntaxGenerator generator,
                SemanticModel semanticModel)
            {
                var operation = semanticModel.GetOperation(expression);
                SyntaxNode newLiteralExpression;

                if (operation?.Kind == OperationKind.Literal && operation.ConstantValue.HasValue && operation.ConstantValue.Value is true)
                {
                    newLiteralExpression = generator.FalseLiteralExpression();
                }
                else if (operation?.Kind == OperationKind.Literal && operation.ConstantValue.HasValue && operation.ConstantValue.Value is false)
                {
                    newLiteralExpression = generator.TrueLiteralExpression();
                }
                else
                {
                    newLiteralExpression = generator.LogicalNotExpression(expression.WithoutTrivia());
                }

                return newLiteralExpression.WithTriviaFrom(expression);
            }

            private static SyntaxNode GetNegationOfLogicalNotExpression(
                SyntaxNode expression,
                ISyntaxFactsService syntaxFacts)
            {
                var operatorToken = syntaxFacts.GetOperatorTokenOfPrefixUnaryExpression(expression);
                var operand = syntaxFacts.GetOperandOfPrefixUnaryExpression(expression);

                return operand.WithPrependedLeadingTrivia(operatorToken.LeadingTrivia);
            }
        }
    }
}
