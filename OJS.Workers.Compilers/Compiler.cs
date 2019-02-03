namespace OJS.Workers.Compilers
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;

    using OJS.Workers.Common;
    using OJS.Workers.Common.Models;
    using Serilog;

    /// <summary>
    /// Defines the base of the work with compilers algorithm and allow the subclasses to implement some of the algorithm parts.
    /// </summary>
    /// <remarks>Template method design pattern is used.</remarks>
    public abstract class Compiler : ICompiler
    {

        protected const string CompilationDirectoryName = "CompilationDir";

        private static readonly Random Rnd = new Random();

        private readonly ILogger logger;


        protected Compiler(int processExitTimeOutMultiplier)
        {
            this.logger = Log.Logger;
            this.MaxProcessExitTimeOutInMilliseconds =
                Constants.DefaultProcessExitTimeOutMilliseconds * processExitTimeOutMultiplier;

            this.logger.Information(
                "Instantiating compiler {Compiler} with MaxProcessExitTimeOutInMilliseconds: {MaxProcessExitTimeOutInMilliseconds}",
                this.GetType().Name,
                this.MaxProcessExitTimeOutInMilliseconds);
        }

        public virtual bool ShouldDeleteSourceFile => true;

        public virtual int MaxProcessExitTimeOutInMilliseconds { get; }

        protected string CompilationDirectory { get; set; }

        public static ICompiler CreateCompiler(CompilerType compilerType)
        {
            Log.Logger.Information("Creating compiler of type {@CompilerType}", compilerType);

            switch (compilerType)
            {
                case CompilerType.None:
                    return null;
                case CompilerType.CSharp:
                    return new CSharpCompiler(Settings.CSharpCompilerProcessExitTimeOutMultiplier);
                case CompilerType.CSharpDotNetCore:
                    return new CSharpDotNetCoreCompiler(
                        Settings.CSharpDotNetCoreCompilerProcessExitTimeOutMultiplier,
                        Settings.CSharpDotNetCoreCompilerPath,
                        Settings.DotNetCoreSharedAssembliesPath);
                case CompilerType.CPlusPlusGcc:
                    return new CPlusPlusCompiler(Settings.CPlusPlusCompilerProcessExitTimeOutMultiplier);
                case CompilerType.MsBuild:
                    return new MsBuildCompiler(Settings.MsBuildCompilerProcessExitTimeOutMultiplier);
                case CompilerType.Java:
                    return new JavaCompiler(Settings.JavaCompilerProcessExitTimeOutMultiplier);
                case CompilerType.JavaZip:
                    return new JavaZipCompiler(Settings.JavaZipCompilerProcessExitTimeOutMultiplier);
                case CompilerType.JavaInPlaceCompiler:
                    return new JavaInPlaceFolderCompiler(Settings.JavaInPlaceCompilerProcessExitTimeOutMultiplier);
                case CompilerType.MsBuildLibrary:
                    return new MsBuildLibraryCompiler(Settings.MsBuildLibraryCompilerProcessExitTimeOutMultiplier);
                case CompilerType.CPlusPlusZip:
                    return new CPlusPlusZipCompiler(Settings.CPlusPlusZipCompilerProcessExitTimeOutMultiplier);
                case CompilerType.DotNetCompiler:
                    return new DotNetCompiler(Settings.DotNetCompilerProcessExitTimeOutMultiplier);
                case CompilerType.SolidityCompiler:
                    return new SolidityCompiler(Settings.SolidityCompilerProcessExitTimeOutMultiplier);
                default:
                    throw new ArgumentException("Unsupported compiler.");
            }
        }

        public virtual CompileResult Compile(
            string compilerPath,
            string inputFile,
            string additionalArguments)
        {

            this.logger.Information(
                "Compiling with args {CompilerPath}, {InputFile}, {AdditionalArguments}",
                compilerPath,
                inputFile,
                additionalArguments);

            if (compilerPath == null)
            {
                throw new ArgumentNullException(nameof(compilerPath));
            }

            if (inputFile == null)
            {
                throw new ArgumentNullException(nameof(inputFile));
            }

            if (!File.Exists(compilerPath))
            {
                return new CompileResult(false, $"Compiler not found! Searched in: {compilerPath}");
            }

            if (!File.Exists(inputFile))
            {
                return new CompileResult(false, $"Input file not found! Searched in: {inputFile}");
            }

            this.CompilationDirectory = $"{Path.GetDirectoryName(inputFile)}\\{CompilationDirectoryName}";
            Directory.CreateDirectory(this.CompilationDirectory);

            this.logger.Information(
                "Created compilation directory {CompilationDirectory}",
                this.CompilationDirectory);

            // Move source file if needed
            string newInputFilePath = this.RenameInputFile(inputFile);
            if (newInputFilePath != inputFile)
            {
                this.logger.Information(
                    "Moving the input file {InputFile} to a new path {NewInputFilePath}",
                    inputFile,
                    newInputFilePath);
                File.Move(inputFile, newInputFilePath);
                inputFile = newInputFilePath;
            }

            // Build compiler arguments
            var outputFile = this.GetOutputFileName(inputFile);
            var arguments = this.BuildCompilerArguments(inputFile, outputFile, additionalArguments);

            // Find compiler directory
            var directoryInfo = new FileInfo(compilerPath).Directory;
            if (directoryInfo == null)
            {
                return new CompileResult(false, $"Compiler directory is null. Compiler path value: {compilerPath}");
            }

            // Prepare process start information
            var processStartInfo = this.SetCompilerProcessStartInfo(compilerPath, directoryInfo, arguments);

            // Execute compiler
            this.logger.Information(
                "Executing the compiler with arguments: {@ProcessStartInfo}",
                processStartInfo);
            var compilerOutput = ExecuteCompiler(processStartInfo, this.MaxProcessExitTimeOutInMilliseconds);

            if (this.ShouldDeleteSourceFile)
            {
                if (File.Exists(newInputFilePath))
                {
                    File.Delete(newInputFilePath);
                }
            }

            // Check results and return CompilerResult instance
            if (!compilerOutput.IsSuccessful)
            {
                // Compiled file is missing
                this.logger.Warning("The compilation was unsuccessful!");
                return new CompileResult(false, $"Compiled file is missing. Compiler output: {compilerOutput.Output}");
            }

            outputFile = this.ChangeOutputFileAfterCompilation(outputFile);

            this.logger.Information(
                "The compilation was successful! Output file: {OutputFile}",
                outputFile);

            if (!string.IsNullOrWhiteSpace(compilerOutput.Output))
            {
                // Compile file is ready but the compiler has something on standard error (possibly compile warnings)
                return new CompileResult(true, compilerOutput.Output, outputFile);
            }

            // Compilation is ready without warnings
            return new CompileResult(outputFile);
        }

        public virtual string RenameInputFile(string inputFile)
        {
            return inputFile;
        }

        public virtual string GetOutputFileName(string inputFileName)
        {
            return inputFileName + ".exe";
        }

        public virtual string ChangeOutputFileAfterCompilation(string outputFile)
        {
            return outputFile;
        }

        public abstract string BuildCompilerArguments(string inputFile, string outputFile, string additionalArguments);

        public virtual ProcessStartInfo SetCompilerProcessStartInfo(string compilerPath, DirectoryInfo directoryInfo, string arguments)
        {
            return new ProcessStartInfo(compilerPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = directoryInfo.ToString(),
                Arguments = arguments,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        protected static CompilerOutput ExecuteCompiler(
            ProcessStartInfo compilerProcessStartInfo,
            int processExitTimeOutMillisecond)
        {
            var outputBuilder = new StringBuilder();
            var errorOutputBuilder = new StringBuilder();
            int exitCode;

            var outputWaitHandle = new AutoResetEvent(false);
            var errorWaitHandle = new AutoResetEvent(false);
            var compilerTimedOut = false;
            var thisThreadId = Thread.CurrentThread.ManagedThreadId;
            
            using (outputWaitHandle)
            {
                using (errorWaitHandle)
                {
                    using (var process = new Process())
                    {
                        process.StartInfo = compilerProcessStartInfo;

                        var outputHandle = new DataReceivedEventHandler((sender, e) =>
                        {
                            Log.Logger.Information("{CompilerThreadId}: Received output data {CompilerOutputData}", thisThreadId, e.Data);
                            if (e.Data == null)
                            {
                                outputWaitHandle.Set();
                            }
                            else
                            {
                                lock (outputBuilder)
                                {
                                    outputBuilder.AppendLine(e.Data);
                                }
                            }
                        });

                        var errorHandle = new DataReceivedEventHandler((sender, e) =>
                        {
                            Log.Logger.Information("{CompilerThreadId}: Received error data {CompilerErrorData}", thisThreadId, e.Data);

                            if (e.Data == null)
                            {
                                errorWaitHandle.Set();
                            }
                            else
                            {
                                lock (errorOutputBuilder)
                                {
                                    errorOutputBuilder.AppendLine(e.Data);
                                }
                            }
                        });

                        process.OutputDataReceived += outputHandle;
                        process.ErrorDataReceived += errorHandle;

                        Log.Logger.Information("Starting compiler's process");

                        Thread.Sleep(Rnd.Next(100, 500));

                        var started = process.Start();
                        if (!started)
                        {
                            Log.Logger.Warning("Could not start compiler");
                            return new CompilerOutput(1, "Could not start compiler.");
                        }

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        var exited = process.WaitForExit(processExitTimeOutMillisecond);
                        if (!exited)
                        {
                            Log.Logger.Warning(
                                "Waiting compiler's process reached the timeout of {CompilerProcessWaitTimeout}",
                                processExitTimeOutMillisecond);

                            //process.CancelOutputRead();
                            //process.CancelErrorRead();

                            // Double check if the process has exited before killing it
                            if (!process.HasExited)
                            {
                                Log.Logger.Warning("Killing compiler's process");
                                process.Kill();
                            }

                            // return new CompilerOutput(1, "Compiler process timed out.");
                            compilerTimedOut = true;
                        }

                        Log.Logger.Warning("Calling WaitForExit()");

                        // https://github.com/dotnet/corefx/issues/12219#issuecomment-252324082
                        process.WaitForExit();
                        Log.Logger.Warning("Done calling WaitForExit()");

                        if (outputWaitHandle.WaitOne(2000) == false)
                        {
                            Log.Logger.Warning($"Waiting on {nameof(outputWaitHandle)} has timed out!");
                        }

                        if (errorWaitHandle.WaitOne(2000) == false)
                        {
                            Log.Logger.Warning($"Waiting on {nameof(errorWaitHandle)} has timed out!");
                        }

                        process.OutputDataReceived -= outputHandle;
                        process.ErrorDataReceived -= errorHandle;
                        exitCode = process.ExitCode;
                    }
                }
            }

            var output = outputBuilder.ToString().Trim();
            var errorOutput = errorOutputBuilder.ToString().Trim();

            var compilerOutput = $"{output}{Environment.NewLine}{errorOutput}".Trim();

            if (exitCode != 0 && string.IsNullOrEmpty(compilerOutput))
            {
                compilerOutput = "The compiler detected an error in your code but was unable to display it. Please submit your core again!";
            }

            if (compilerTimedOut == true)
            {
                Log.Logger.Warning(
                    "Compiler's process has timed out with output: {CompilerOutputTimeout}",
                    compilerOutput);

                return new CompilerOutput(1, "Compiler process timed out. Please, submit your solution again!");
            }

            Log.Logger.Information(
                "Compiler's process completed with exit code {CompilerExitCode} and the following output: {CompilerOutput}",
                exitCode,
                compilerOutput);

            return new CompilerOutput(exitCode, compilerOutput);
        }
    }
}
