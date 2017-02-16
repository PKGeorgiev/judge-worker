﻿namespace OJS.Workers.ExecutionStrategies
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Common;
    using Microsoft.Build.Evaluation;

    using OJS.Common.Extensions;
    using OJS.Common.Models;
    using OJS.Workers.Checkers;
    using OJS.Workers.Executors;

    public class CSharpUnitTestsRunnerExecutionStrategy : ExecutionStrategy
    {
        private const string ZippedSubmissionName = "Submission.zip";
        private const string TestedCode = "TestedCode.cs";
        private const string CsProjFileSearchPattern = "*.csproj";
        private const string ProjFileExtenstion = ".csproj";
        private const string DllFileExtension = ".dll";

        public CSharpUnitTestsRunnerExecutionStrategy(string nUnitConsoleRunnerPath)
        {
            this.NUnitConsoleRunnerPath = nUnitConsoleRunnerPath;
            this.WorkingDirectory = DirectoryHelpers.CreateTempDirectory();
        }

        ~CSharpUnitTestsRunnerExecutionStrategy()
        {
            DirectoryHelpers.SafeDeleteDirectory(this.WorkingDirectory, true);
        }

        protected string NUnitConsoleRunnerPath { get; set; }

        protected string WorkingDirectory { get; set; }

        public override ExecutionResult Execute(ExecutionContext executionContext)
        {
            ExecutionResult result = new ExecutionResult();
            byte[] userSubmissionContent = executionContext.FileContent;

            var submissionFilePath = $"{this.WorkingDirectory}\\{ZippedSubmissionName}";
            File.WriteAllBytes(submissionFilePath, userSubmissionContent);
            FileHelpers.UnzipFile(submissionFilePath, this.WorkingDirectory);
            File.Delete(submissionFilePath);

            string csProjFilePath = FileHelpers.FindFirstFileMatchingPattern(
                this.WorkingDirectory,
                CsProjFileSearchPattern);

            // Edit References in Project file
            var project = new Project(csProjFilePath);
            this.CorrectProjectReferences(project);
            project.Save(csProjFilePath);
            project.ProjectCollection.UnloadAllProjects();

            // Initially set isCompiledSucessfully to true
            result.IsCompiledSuccessfully = true;

            var executor = new RestrictedProcessExecutor();
            var checker = Checker.CreateChecker(
                executionContext.CheckerAssemblyName,
                executionContext.CheckerTypeName,
                executionContext.CheckerParameter);

            result = this.RunUnitTests(executionContext, executor, checker, result, csProjFilePath);
            return result;
        }

        private ExecutionResult RunUnitTests(
            ExecutionContext executionContext, 
            IExecutor executor,
            IChecker checker,
            ExecutionResult result, 
            string csProjFilePath)
        {
            var compileDirectory = Path.GetDirectoryName(csProjFilePath);
            int originalTestsPassed = -1;
            int count = 0;
           
            foreach (var test in executionContext.Tests)
            {
                // Copy the test input into a .cs file
                var testedCodePath = $"{compileDirectory}\\{TestedCode}";
                File.WriteAllText(testedCodePath, test.Input);

                // Compile the project
                var project = new Project(csProjFilePath);
                var didCompile = project.Build();
                project.ProjectCollection.UnloadAllProjects();

                // If a test does not compile, set isCompiledSuccessfully to false and break execution
                if (!didCompile)
                {
                    result.IsCompiledSuccessfully = false;
                    return result;
                }

                var fileName = Path.GetFileName(csProjFilePath);
                fileName = fileName.Replace(ProjFileExtenstion, DllFileExtension);
                var dllPath = FileHelpers.FindFirstFileMatchingPattern(this.WorkingDirectory, fileName);
                var arguments = new List<string> { dllPath };
                arguments.AddRange(executionContext.AdditionalCompilerArguments.Split(' '));

                // Run unit tests on the resulting .dll
                var processExecutionResult = executor.Execute(
                    this.NUnitConsoleRunnerPath,
                    string.Empty,
                    executionContext.TimeLimit,
                    executionContext.MemoryLimit,
                    arguments);

                // Construct and figure out what the Test result is
                TestResult testResult = new TestResult()
                {
                    Id = test.Id,
                    TimeUsed = (int)processExecutionResult.TimeWorked.TotalMilliseconds,
                    MemoryUsed = (int)processExecutionResult.MemoryUsed
                };

                switch (processExecutionResult.Type)
                {
                    case ProcessExecutionResultType.RunTimeError:
                        testResult.ResultType = TestRunResultType.RunTimeError;
                        testResult.ExecutionComment = processExecutionResult.ErrorOutput.MaxLength(2048); // Trimming long error texts
                        break;
                    case ProcessExecutionResultType.TimeLimit:
                        testResult.ResultType = TestRunResultType.TimeLimit;
                        break;
                    case ProcessExecutionResultType.MemoryLimit:
                        testResult.ResultType = TestRunResultType.MemoryLimit;
                        break;
                    case ProcessExecutionResultType.Success:
                        int totalTests = 0;
                        int passedTests = 0;
                 
                        this.ExtractTestResult(processExecutionResult.ReceivedOutput, ref passedTests, ref totalTests);
                        var message = "Test Passed!";

                        if (totalTests == 0)
                        {
                            message = "No tests found";
                        }
                        else if (passedTests == originalTestsPassed)
                        {
                            message = "No functionality covering this test!";
                        }

                        if (count == 0)
                        {
                            originalTestsPassed = passedTests;
                            if (totalTests != passedTests)
                            {
                                message = "Not all tests passed on the correct solution.";
                            }
                        }

                        var checkerResult = checker.Check(test.Input, message, test.Output, test.IsTrialTest);
                        testResult.ResultType = checkerResult.IsCorrect 
                            ? TestRunResultType.CorrectAnswer 
                            : TestRunResultType.WrongAnswer;
                        testResult.CheckerDetails = checkerResult.CheckerDetails;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(processExecutionResult), 
                            @"Invalid ProcessExecutionResultType value.");
                }

                // Cleanup the .cs with the tested code to prepare for the next test
                File.Delete(testedCodePath);
                result.TestResults.Add(testResult);
                count++;
            }

            return result;
        }

        private void ExtractTestResult(string receivedOutput, ref int passedTests, ref int totalTests)
        {
            Regex testResultsRegex =
                new Regex(
                    @"Test Count: (\d+), Passed: (\d+), Failed: (\d+), Warnings: \d+, Inconclusive: \d+, Skipped: \d+");
            var res = testResultsRegex.Match(receivedOutput);
            totalTests = int.Parse(res.Groups[1].Value);
            passedTests = int.Parse(res.Groups[2].Value);
        }

        private void CorrectProjectReferences(Project project)
        {
            // Remove the first Project Reference (this should be the reference to the tested project)
            var projectReference = project.GetItems("ProjectReference").FirstOrDefault();
            if (projectReference != null)
            {
                project.RemoveItem(projectReference);
            }

            // Add a reference to tested code as a .cs file
            project.AddItem("Compile", TestedCode);

            // Remove previous NUnit reference (the path is probably pointing to the users package folder)
            var nUnitPrevReference = project.Items.FirstOrDefault(x => x.EvaluatedInclude.Contains("nunit.framework"));
            if (nUnitPrevReference != null)
            {
                project.RemoveItem(nUnitPrevReference);
            }

            // Add our NUnit Reference, if private is false, the .dll will not be copied and the tests will not run
            var nUnitMetaData = new Dictionary<string, string>();
            nUnitMetaData.Add("Private", "True");
            project.AddItem(
                "Reference",
                "nunit.framework, Version=3.6.0.0, Culture=neutral, PublicKeyToken=2638cd05610744eb, processorArchitecture=MSIL",
                nUnitMetaData);
    
            // If we use NUnit we don't really need the VSTT, it will save us copying of the .dll
            var vsTestFrameworkReference = project.Items
                .FirstOrDefault(x => 
                    x.EvaluatedInclude.Contains("Microsoft.VisualStudio.QualityTools.UnitTestFramework"));

            if (vsTestFrameworkReference != null)
            {
                project.RemoveItem(vsTestFrameworkReference);
            }
        }
    }
}