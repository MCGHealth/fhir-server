﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.SqlServer
{
    public class SqlQueryBuilder : IExpressionVisitor
    {
        private readonly Dictionary<string, short> _resourceTypeToId;
        private readonly Dictionary<(string, byte?), short> _searchParamUrlToId;
        private readonly SqlQueryParameterManager _sqlQueryParameterManager;
        private readonly CteManager _cteManager;
        private readonly StringBuilder _query;
        private SearchParameter _currentSearchParameter;
        private string _currentTableAlias;

        private SqlQueryBuilder(
            Dictionary<string, short> resourceTypeToId,
            Dictionary<(string, byte?), short> searchParamUrlToId,
            StringBuilder query,
            SqlQueryParameterManager sqlQueryParameterManager,
            CteManager cteManager)
        {
            _resourceTypeToId = resourceTypeToId;
            _searchParamUrlToId = searchParamUrlToId;
            _query = query;
            _sqlQueryParameterManager = sqlQueryParameterManager;
            _cteManager = cteManager;
        }

        public static string BuildQuery(Dictionary<string, short> resourceTypeToId, Dictionary<(string, byte?), short> searchParamUrlToId, SearchOptions searchOptions, SqlParameterCollection parameterCollection, int pageSize, int pageNum)
        {
            var cteManager = new CteManager();
            var query = new StringBuilder(@"
SELECT ResourceTypePK, Id, Version, LastUpdated, RawResource FROM RESOURCE r 
WHERE ");
            if (searchOptions.Expression != null)
            {
                var builder = new SqlQueryBuilder(
                    resourceTypeToId,
                    searchParamUrlToId,
                    query,
                    new SqlQueryParameterManager(parameterCollection),
                    cteManager);

                searchOptions.Expression.AcceptVisitor(builder);

                query.Append("AND ");
            }

            if (cteManager.Definitions.Count > 0)
            {
                var newQuery = new StringBuilder("WITH ");
                for (int i = cteManager.Definitions.Count - 1; i >= 0; i--)
                {
                    newQuery.Append(cteManager.Definitions[i]);
                    if (i != 0)
                    {
                        newQuery.AppendLine(",");
                    }
                }

                newQuery.Append(query);
                query = newQuery;
            }

            AppendLatestVersionPredicate(query);

            query.AppendLine(@"ORDER BY ResourcePK
OFFSET ((@pageNum) * @pagesize) ROWS
FETCH NEXT @pageSize ROWS ONLY;
");

            parameterCollection.AddWithValue("@pageNum", pageNum);
            parameterCollection.AddWithValue("@pageSize", pageSize);

            return query.ToString();
        }

        private static void AppendLatestVersionPredicate(StringBuilder query)
        {
            query.AppendLine(@"NOT EXISTS (
 SELECT * FROM Resource r2
 WHERE r.ResourcePK = r2.ResourcePK
 AND r2.Version > r.Version
)");
        }

        public void Visit(SearchParameterExpression expression)
        {
            SearchParameter currentParameterSnapshot = _currentSearchParameter;
            string currentTableAliasSnapshot = _currentTableAlias;
            _currentTableAlias = "i";
            _currentSearchParameter = expression.Parameter;
            try
            {
                if (expression.Parameter.Name == SearchParameterNames.ResourceType)
                {
                    expression.Expression.AcceptVisitor(this);
                }
                else
                {
                    if (_currentSearchParameter.Type == SearchParamType.Composite)
                    {
                        expression.Expression.AcceptVisitor(this);
                    }
                    else
                    {
                        GenerateSubquery(true, _searchParamUrlToId[(_currentSearchParameter.Url, (byte?)null)], expression.Expression);
                    }
                }

                _query.AppendLine();
            }
            finally
            {
                _currentSearchParameter = currentParameterSnapshot;
                _currentTableAlias = currentTableAliasSnapshot;
            }
        }

        public void Visit(CompositeComponentSearchParameterExpression expression)
        {
            SearchParameter currentParameterSnapshot = _currentSearchParameter;
            string currentTableAliasSnapshot = _currentTableAlias;
            _currentTableAlias = "c" + expression.ComponentIndex;
            _currentSearchParameter = expression.Parameter;
            try
            {
                Debug.Assert(currentParameterSnapshot.Type == SearchParamType.Composite, "Expected composite search parameter to be the parent of a composite component search parameter.");

                byte? componentIndex = (byte?)expression.ComponentIndex;
                GenerateSubquery(false, _searchParamUrlToId[(currentParameterSnapshot.Url, componentIndex)], expression.Expression);
                if (expression.ComponentIndex > 0)
                {
                    _query.Append(" AND c1.CompositeCorrelationId = ").Append(_currentTableAlias).AppendLine(".CompositeCorrelationId").AppendLine(")");
                }

                if (currentParameterSnapshot.Component.Count == expression.ComponentIndex + 1)
                {
                    _query.AppendLine(")");
                }
            }
            finally
            {
                _currentSearchParameter = currentParameterSnapshot;
                _currentTableAlias = currentTableAliasSnapshot;
            }
        }

        private void GenerateSubquery(bool closeParenthesis, short searchParamId, Expression innerExpression)
        {
            _query.Append(@" EXISTS(
SELECT *
FROM ")
                .Append(TableName(_currentSearchParameter)).Append(" ").AppendLine(_currentTableAlias).Append("WHERE ").Append(_currentTableAlias).Append(@".ResourcePK = r.ResourcePK
AND ").Append(_currentTableAlias).Append(@".SearchParamPK = ").AppendLine(_sqlQueryParameterManager.CreateParameter(searchParamId));

            if (innerExpression != null)
            {
                _query.Append("AND ");
                innerExpression.AcceptVisitor(this);
            }

            if (closeParenthesis)
            {
                _query.Append(')');
            }
        }

        private static string TableName(SearchParameter expressionParameter)
        {
            switch (expressionParameter.Type)
            {
                case SearchParamType.Number:
                    return "dbo.NumberSearchParam";
                case SearchParamType.Date:
                    return "dbo.DateSearchParam";
                case SearchParamType.String:
                    return "dbo.StringSearchParam";
                case SearchParamType.Token:
                    return "dbo.TokenSearchParam";
                case SearchParamType.Reference:
                    return "dbo.ReferenceSearchParam";
                case SearchParamType.Composite:
                    throw new NotSupportedException();
                case SearchParamType.Quantity:
                    return "dbo.QuantitySearchParam";
                case SearchParamType.Uri:
                    return "dbo.UriSearchParam";
                default:
                    throw new ArgumentOutOfRangeException(nameof(expressionParameter));
            }
        }

        public void Visit(BinaryExpression expression)
        {
            if (_currentSearchParameter.Name == SearchParameterNames.ResourceType)
            {
                _query.Append("r.ResourceTypePK = ").AppendLine(_sqlQueryParameterManager.CreateParameter(_resourceTypeToId[(string)expression.Value]));
            }
            else
            {
                _query.Append(_currentTableAlias).Append(".").Append(ColumnName(expression.FieldName));
                switch (expression.BinaryOperator)
                {
                    case BinaryOperator.Equal:
                        _query.Append(" = ");
                        break;
                    case BinaryOperator.GreaterThan:
                        _query.Append(" > ");
                        break;
                    case BinaryOperator.GreaterThanOrEqual:
                        _query.Append(" >= ");
                        break;
                    case BinaryOperator.LessThan:
                        _query.Append(" < ");
                        break;
                    case BinaryOperator.LessThanOrEqual:
                        _query.Append(" <= ");
                        break;
                    case BinaryOperator.NotEqual:
                        _query.Append(" <> ");
                        break;
                    default:
                        throw new InvalidOperationException(expression.BinaryOperator.ToString());
                }

                _query.Append(_sqlQueryParameterManager.CreateParameter(expression.Value));
            }
        }

        public void Visit(ChainedExpression expression)
        {
            string cteName = $"cte_{expression.Parameter.Name}_{expression.TargetResourceType}_{_cteManager.Definitions.Count}";
            var cteBody = new StringBuilder(cteName).Append(@"(Id)
AS (
SELECT Id from dbo.Resource r 
WHERE r.ResourceTypePK = ").AppendLine(_sqlQueryParameterManager.CreateParameter(_resourceTypeToId[expression.TargetResourceType.ToString()]))
                .Append("AND ");

            _cteManager.Definitions.Add(cteBody);

            var cteQueryBuilder = new SqlQueryBuilder(_resourceTypeToId, _searchParamUrlToId, cteBody, _sqlQueryParameterManager, _cteManager);
            expression.Expression.AcceptVisitor(cteQueryBuilder);

            cteBody.AppendLine();
            cteBody.Append("AND ");
            AppendLatestVersionPredicate(cteBody);

            cteBody.AppendLine().AppendLine(")");

            SearchParameter currentSearchParameterSnapshot = _currentSearchParameter;
            string currentTableAliasSnapshot = _currentTableAlias;
            _currentTableAlias = "i";
            _currentSearchParameter = expression.Parameter;
            try
            {
                var criteria = Expression.And(Expression.Missing(FieldName.ReferenceBaseUri), Expression.StringEquals(FieldName.ReferenceResourceType, expression.TargetResourceType.ToString(), false));
                GenerateSubquery(false, _searchParamUrlToId[(expression.Parameter.Url, null)], criteria);
                _query.AppendLine().AppendLine(@"AND EXISTS(SELECT * FROM ").Append(cteName)
                    .Append(" WHERE ").Append(cteName).Append(".Id = ")
                    .Append(_currentTableAlias).Append(".")
                    .Append(ColumnName(FieldName.ReferenceResourceId))
                    .AppendLine(")").AppendLine(")");
            }
            finally
            {
                _currentTableAlias = currentTableAliasSnapshot;
                _currentSearchParameter = currentSearchParameterSnapshot;
            }
        }

        public void Visit(MissingFieldExpression expression)
        {
            string columnName = ColumnName(expression.FieldName);
            _query.Append(_currentTableAlias).Append(".").Append(columnName).Append(" IS NULL");
        }

        public void Visit(MissingSearchParameterExpression expression)
        {
            SearchParameter currentSearchParameterSnapshot = _currentSearchParameter;
            _currentSearchParameter = expression.Parameter;
            string currentTableAliasSnapshot = _currentTableAlias;
            _currentTableAlias = "i";
            try
            {
                if (expression.IsMissing)
                {
                    _query.Append(" NOT ");
                }

                GenerateSubquery(true, _searchParamUrlToId[(expression.Parameter.Url, (byte?)null)], null);
            }
            finally
            {
                _currentSearchParameter = currentSearchParameterSnapshot;
                _currentTableAlias = currentTableAliasSnapshot;
            }
        }

        public void Visit(MultiaryExpression expression)
        {
            MultiaryOperator op = expression.MultiaryOperation;
            IReadOnlyList<Expression> expressions = expression.Expressions;
            string operation;

            switch (op)
            {
                case MultiaryOperator.And:
                    operation = "AND";
                    break;
                case MultiaryOperator.Or:
                    operation = "OR";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(expression));
            }

            if (op == MultiaryOperator.Or)
            {
                _query.Append("(");
            }

            for (int i = 0; i < expressions.Count; i++)
            {
                // Output each expression.
                expressions[i].AcceptVisitor(this);

                if (i != expressions.Count - 1)
                {
                    if (!char.IsWhiteSpace(_query[_query.Length - 1]))
                    {
                        _query.Append(" ");
                    }

                    _query.Append(operation).Append(" ");
                }
            }

            if (op == MultiaryOperator.Or)
            {
                _query.Append(")");
            }
        }

        public void Visit(StringExpression expression)
        {
            string columnName = ColumnName(expression.FieldName);

            object expressionValue = expression.Value;
            if (expression.FieldName == FieldName.TokenText)
            {
                string tokenTextAlias = _currentTableAlias + "t";
                _query.Append(@"EXISTS (
SELECT * 
FROM dbo.TokenText ").AppendLine(tokenTextAlias).Append("WHERE ").Append(_currentTableAlias).Append(@".").Append(columnName).Append(@" = ").Append(tokenTextAlias).Append(@".Hash
AND ").Append(tokenTextAlias).Append(".Text LIKE ").AppendLine(_sqlQueryParameterManager.CreateParameter($@"%{expressionValue}%")).AppendLine(")");
                return;
            }

            if (expression.FieldName == FieldName.ReferenceBaseUri)
            {
                string uriAlias = _currentTableAlias + "u";
                _query.Append(@"EXISTS (
SELECT * 
FROM dbo.Uri ").AppendLine(uriAlias).Append("WHERE ").Append(_currentTableAlias).Append(@".").Append(columnName).Append(@" = ").Append(uriAlias).Append(@".UriPK
AND ").Append(uriAlias).Append(".Uri = ").AppendLine(_sqlQueryParameterManager.CreateParameter(expressionValue)).AppendLine(")");
                return;
            }

            _query.Append(_currentTableAlias).Append(".").Append(columnName);

            if (expression.StringOperator == StringOperator.Equals)
            {
                SqlDbType? type = null;
                if (expression.FieldName == FieldName.ReferenceResourceType)
                {
                    expressionValue = _resourceTypeToId[(string)expressionValue];
                }
                else if (expression.FieldName == FieldName.ReferenceResourceId)
                {
                    type = SqlDbType.VarChar;
                }

                _query.Append(" = ").AppendLine(_sqlQueryParameterManager.CreateParameter(expressionValue, type));
                return;
            }

            switch (expression.StringOperator)
            {
                case StringOperator.NotContains:
                case StringOperator.NotEndsWith:
                case StringOperator.NotStartsWith:
                    _query.Append(" NOT LIKE");
                    break;
                default:
                    _query.Append(" LIKE ");
                    break;
            }

            switch (expression.StringOperator)
            {
                case StringOperator.Contains:
                case StringOperator.NotContains:
                    _query.Append(_sqlQueryParameterManager.CreateParameter($"%{expressionValue}%"));
                    break;
                case StringOperator.EndsWith:
                case StringOperator.NotEndsWith:
                    _query.Append(_sqlQueryParameterManager.CreateParameter($"%{expressionValue}"));
                    break;
                case StringOperator.StartsWith:
                case StringOperator.NotStartsWith:
                    _query.Append(_sqlQueryParameterManager.CreateParameter($"{expressionValue}%"));
                    break;
            }
        }

        private string ColumnName(FieldName fieldName)
        {
            switch (_currentSearchParameter.Type)
            {
                case SearchParamType.Number:
                    switch (fieldName)
                    {
                        case FieldName.Number:
                            return "Number";
                    }

                    break;
                case SearchParamType.Date:
                    switch (fieldName)
                    {
                        case FieldName.DateTimeStart:
                            return "StartTime";
                        case FieldName.DateTimeEnd:
                            return "EndTime";
                    }

                    break;
                case SearchParamType.String:
                    switch (fieldName)
                    {
                        case FieldName.String:
                            return "Value";
                    }

                    break;
                case SearchParamType.Token:
                    switch (fieldName)
                    {
                        case FieldName.TokenSystem:
                            return "System";
                        case FieldName.TokenCode:
                            return "Code";
                        case FieldName.TokenText:
                            return "TextHash";
                    }

                    break;
                case SearchParamType.Reference:
                    switch (fieldName)
                    {
                        case FieldName.ReferenceBaseUri:
                            return "BaseUriPK";
                        case FieldName.ReferenceResourceType:
                            return "ReferenceResourceTypePK";
                        case FieldName.ReferenceResourceId:
                            return "ReferenceResourceId";
                    }

                    break;
                case SearchParamType.Composite:
                    break;
                case SearchParamType.Quantity:
                    switch (fieldName)
                    {
                        case FieldName.Quantity:
                            return "Quantity";
                        case FieldName.QuantityCode:
                            return "Code";
                        case FieldName.QuantitySystem:
                            return "System";
                    }

                    break;
                case SearchParamType.Uri:
                    switch (fieldName)
                    {
                        case FieldName.Uri:
                            return "Uri";
                    }

                    break;
            }

            throw new InvalidOperationException($"Unsupported combination {fieldName}, {_currentSearchParameter.Type}");
        }

        private class CteManager
        {
            public List<StringBuilder> Definitions { get; } = new List<StringBuilder>();
        }
    }
}
