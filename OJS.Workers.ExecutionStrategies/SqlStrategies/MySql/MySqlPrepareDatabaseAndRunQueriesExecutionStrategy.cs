﻿namespace OJS.Workers.ExecutionStrategies.SqlStrategies.MySql
{
    using OJS.Workers.Common;
    using OJS.Workers.ExecutionStrategies.Models;

    public class MySqlPrepareDatabaseAndRunQueriesExecutionStrategy : BaseMySqlExecutionStrategy
    {
        public MySqlPrepareDatabaseAndRunQueriesExecutionStrategy(
            string sysDbConnectionString,
            string restrictedUserId,
            string restrictedUserPassword)
            : base(sysDbConnectionString, restrictedUserId, restrictedUserPassword)
        {
        }

        protected override IExecutionResult<TestResult> ExecuteCompetitive(
            IExecutionContext<TestsInputModel> executionContext)
        {
            return this.Execute(
                executionContext,
                (connection, test, result) =>
                {
                    this.ExecuteNonQuery(connection, test.Input);
                    var sqlTestResult = this.ExecuteReader(connection, executionContext.Code, executionContext.TimeLimit);
                    this.ProcessSqlResult(sqlTestResult, executionContext, test, result);
                });
        }
    }
}
