using System.Net;
using Microsoft.AspNet.SignalR.Client;
using OJS.Services.SignalR;

namespace OJS.Workers.SubmissionProcessors
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    using log4net;

    using Serilog;

    using OJS.Workers.Common;
    using OJS.Workers.Common.Models;
    using OJS.Workers.ExecutionStrategies.Models;
    using OJS.Workers.SubmissionProcessors.Models;

    using OJS.Common.SignalR;

    public class SubmissionProcessor<TSubmission> : ISubmissionProcessor
    {
        private readonly object sharedLockObject;
        private readonly ILogger logger;
        private readonly IDependencyContainer dependencyContainer;
        private readonly ConcurrentQueue<TSubmission> submissionsForProcessing;
        private readonly int portNumber;

        private readonly IOjsHubService hubService;
        
        private ISubmissionProcessingStrategy<TSubmission> submissionProcessingStrategy;
        private bool stopping;

        public SubmissionProcessor(
            string name,
            IDependencyContainer dependencyContainer,
            ConcurrentQueue<TSubmission> submissionsForProcessing,
            int portNumber,
            object sharedLockObject)
        {
            this.Name = name;

            this.logger = dependencyContainer.GetInstance<ILogger>();
            this.logger.Information("{SubmissionProcessor} initializing.", this.Name);

            this.stopping = false;

            this.dependencyContainer = dependencyContainer;
            this.submissionsForProcessing = submissionsForProcessing;
            this.portNumber = portNumber;
            this.sharedLockObject = sharedLockObject;

            this.hubService = dependencyContainer.GetInstance<IOjsHubService>();

            this.logger.Information("{SubmissionProcessor} initialized.", this.Name);
        }

        public string Name { get; set; }

        public void Start()
        {
            this.logger.Information("{SubmissionProcessor} starting...", this.Name);

            //this.hubService.Start();

            //this.logger.Information("{SubmissionProcessor} connected to the OjsHub", this.Name);

            while (!this.stopping)
            {
                using (this.dependencyContainer.BeginDefaultScope())
                {
                    this.submissionProcessingStrategy = this.GetSubmissionProcessingStrategyInstance();

                    var submission = this.GetSubmissionForProcessing();

                    if (submission != null)
                    {
                        this.ProcessSubmission(submission);
                    }
                    else
                    {
                        Thread.Sleep(this.submissionProcessingStrategy.JobLoopWaitTimeInMilliseconds);
                    }
                }
            }

            this.logger.Information("{SubmissionProcessor} stopped.", this.Name);
        }

        public void Stop()
        {
            this.stopping = true;
            //this.hubService.Stop();
        }

        private ISubmissionProcessingStrategy<TSubmission> GetSubmissionProcessingStrategyInstance()
        {
            try
            {
                var processingStrategy = this.dependencyContainer
                    .GetInstance<ISubmissionProcessingStrategy<TSubmission>>();

                processingStrategy.Initialize(
                    this.logger,
                    this.submissionsForProcessing,
                    this.sharedLockObject);

                return processingStrategy;
            }
            catch (Exception ex)
            {
                this.logger.Fatal("Unable to initialize submission processing strategy.", ex);
                throw;
            }
        }

        private IOjsSubmission GetSubmissionForProcessing()
        {
            try
            {
                return this.submissionProcessingStrategy.RetrieveSubmission();
            }
            catch (Exception ex)
            {
                this.logger.Fatal("Unable to get submission for processing.", ex);
                throw;
            }
        }

        // Overload accepting IOjsSubmission and doing cast, because upon getting the submission,
        // TInput is not known and no specific type could be given to the generic ProcessSubmission<>
        private void ProcessSubmission(IOjsSubmission submission)
        {
            try
            {
                switch (submission.ExecutionType)
                {
                    case ExecutionType.TestsExecution:
                        var testsSubmission = (OjsSubmission<TestsInputModel>)submission;
                        this.ProcessSubmission<TestsInputModel, TestResult>(testsSubmission);
                        break;

                    case ExecutionType.SimpleExecution:
                        var simpleSubmission = (OjsSubmission<string>)submission;
                        this.ProcessSubmission<string, OutputResult>(simpleSubmission);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(submission.ExecutionType),
                            "Invalid execution type!");
                }
            }
            catch (Exception ex)
            {
                this.logger.Error(
                    "{SubmissionProcessor}: The method {Method} has thrown an exception on submission {Submission}!",
                    this.Name,
                    nameof(this.ProcessSubmission),
                    ex);

                this.submissionProcessingStrategy.OnError(submission);
            }
        }

        private void ProcessSubmission<TInput, TResult>(OjsSubmission<TInput> submission)
            where TResult : ISingleCodeRunResult, new()
        {
            this.logger.Information("{SubmissionProcessor}: Work on submission {Submission} started.", this.Name, submission.Id);

            this.hubService.NotifyServer(new SrServerNotification()
            {
                Type = SrServerNotificationType.StartedProcessing,
                SubmissionId =  (int)submission.Id                
            });

            this.BeforeExecute(submission);

            var executor = new SubmissionExecutor(this.portNumber);

            var executionResult = executor.Execute<TInput, TResult>(submission);

            this.logger.Information("{SubmissionProcessor}: Work on submission {Submission} ended.", this.Name, submission.Id);

            this.hubService.NotifyServer(new SrServerNotification()
            {
                Type = SrServerNotificationType.FinishedProcessing,
                SubmissionId = (int)submission.Id
            });

            this.ProcessExecutionResult(executionResult, submission);

            this.logger.Information("{SubmissionProcessor}: Submission {Submission} was processed successfully.", this.Name, submission.Id);
        }

        private void BeforeExecute(IOjsSubmission submission)
        {
            try
            {
                this.submissionProcessingStrategy.BeforeExecute();
            }
            catch (Exception ex)
            {
                submission.ProcessingComment = $"Exception before executing the submission: {ex.Message}";

                throw new Exception($"Exception in {nameof(this.submissionProcessingStrategy.BeforeExecute)}", ex);
            }
        }

        private void ProcessExecutionResult<TOutput>(IExecutionResult<TOutput> executionResult, IOjsSubmission submission)
            where TOutput : ISingleCodeRunResult, new()
        {
            try
            {
                this.submissionProcessingStrategy.ProcessExecutionResult(executionResult);
            }
            catch (Exception ex)
            {
                submission.ProcessingComment = $"Exception in processing execution result: {ex.Message}";

                throw new Exception($"Exception in {nameof(this.ProcessExecutionResult)}", ex);
            }
        }
    }
}