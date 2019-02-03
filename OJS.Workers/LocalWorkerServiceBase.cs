﻿namespace OJS.Workers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.ServiceProcess;
    using System.Threading;

    using log4net;

    using Serilog;

    using OJS.Workers.Common;
    using OJS.Workers.SubmissionProcessors;

    public class LocalWorkerServiceBase<TSubmission> : ServiceBase
    {
        private readonly ICollection<Thread> threads;
        private readonly ICollection<ISubmissionProcessor> submissionProcessors;

        protected LocalWorkerServiceBase()
        {
            var loggerAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly();

            this.threads = new List<Thread>();
            this.submissionProcessors = new List<ISubmissionProcessor>();
        }

        protected ILogger Logger { get; private set; }

        protected IDependencyContainer DependencyContainer { get; private set; }

        protected override void OnStart(string[] args)
        {
            this.DependencyContainer = this.GetDependencyContainer();

            this.Logger = this.DependencyContainer.GetInstance<ILogger>();

            this.Logger.Information("{Service} is starting...", Constants.LocalWorkerServiceName);

            this.BeforeStartingThreads();

            this.StartThreads();

            this.Logger.Information("{Service} started", Constants.LocalWorkerServiceName);
        }

        protected override void OnStop()
        {
            this.Logger.Information("{Service} is stopping...", Constants.LocalWorkerServiceName);

            this.BeforeAbortingThreads();

            this.AbortThreads();

            this.Logger.Information("{Service} stopped", Constants.LocalWorkerServiceName);
        }

        protected virtual void BeforeStartingThreads()
        {
            this.SpawnSubmissionProcessorsAndThreads();

            this.CreateExecutionStrategiesWorkingDirectory();
        }

        protected virtual void BeforeAbortingThreads()
        {
            this.StopSubmissionProcessors();

            Thread.Sleep(this.TimeBeforeAbortingThreadsInMilliseconds);
        }

        protected virtual IDependencyContainer GetDependencyContainer() =>
            throw new InvalidOperationException(
                $"{nameof(this.GetDependencyContainer)} method required but not implemented in derived service");

        protected virtual int TimeBeforeAbortingThreadsInMilliseconds =>
            Constants.DefaultTimeBeforeAbortingThreadsInMilliseconds;

        private void SpawnSubmissionProcessorsAndThreads()
        {
            var submissionsForProcessing = new ConcurrentQueue<TSubmission>();
            var sharedLockObject = new object();

            var threadCount = Environment.ProcessorCount * Settings.ThreadsCount;

            this.Logger.Information(
                "Spawning {SubmissionThreadCount} threads to process submissions (Processor count: {ProcessorCount}, Thread count from config: {ThreadCountFromConfig} )", 
                threadCount, 
                Environment.ProcessorCount, 
                Settings.ThreadsCount);

            for (var i = 1; i <= threadCount; i++)
            {
                var submissionProcessor = new SubmissionProcessor<TSubmission>(
                    name: $"SP #{i}",
                    dependencyContainer: this.DependencyContainer,
                    submissionsForProcessing: submissionsForProcessing,
                    portNumber: Settings.GanacheCliDefaultPortNumber + i,
                    sharedLockObject: sharedLockObject);

                var thread = new Thread(submissionProcessor.Start)
                {
                    Name = $"{nameof(Thread)} #{i}"
                };

                this.submissionProcessors.Add(submissionProcessor);
                this.threads.Add(thread);
            }
        }

        private void StartThreads()
        {
            foreach (var thread in this.threads)
            {
                this.Logger.Information("{Thread}: starting...", thread.Name);
                thread.Start();
                this.Logger.Information("{Thread}: started", thread.Name);
                Thread.Sleep(234);
            }
        }

        private void StopSubmissionProcessors()
        {
            foreach (var submissionProcessor in this.submissionProcessors)
            {
                submissionProcessor.Stop();
                this.Logger.Information("{SubmissionProcessor}: stopped", submissionProcessor.Name);
            }
        }

        private void AbortThreads()
        {
            foreach (var thread in this.threads)
            {
                thread.Abort();
                this.Logger.Information("{Thread}: aborted", thread.Name);
            }
        }

        /// <summary>
        /// Creates folder in the Temp directory if not already created,
        /// in which all strategies create their own working directories
        /// making easier the deletion of left-over files by the background job
        /// </summary>
        private void CreateExecutionStrategiesWorkingDirectory()
        {
            var path = Constants.ExecutionStrategiesWorkingDirectoryPath;
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
    }
}