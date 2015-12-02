﻿namespace OJS.Workers.Compilers
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;

    using ICSharpCode.SharpZipLib.Zip;

    using OJS.Common;
    using OJS.Common.Extensions;

    public class JavaZipCompiler : Compiler
    {
        private const string JavaCompiledFilesSearchPattern = "*.class";
        private const string MainClassFileNameSuffix = "\\Main.class";

        private readonly string inputPath;
        private readonly string outputPath;

        public JavaZipCompiler()
        {
            this.inputPath = DirectoryHelpers.CreateTempDirectory();
            this.outputPath = DirectoryHelpers.CreateTempDirectory();
        }

        ~JavaZipCompiler()
        {
            DirectoryHelpers.SafeDeleteDirectory(this.inputPath, true);
            DirectoryHelpers.SafeDeleteDirectory(this.outputPath, true);
        }

        public override string RenameInputFile(string inputFile)
        {
            var inputFileExtension = inputFile.EndsWith(GlobalConstants.ZipFileExtension, StringComparison.InvariantCultureIgnoreCase)
               ? string.Empty
               : GlobalConstants.ZipFileExtension;
            return $"{inputFile}{inputFileExtension}";
        }

        public override string GetOutputFileName(string inputFileName) => inputFileName;

        public override string BuildCompilerArguments(string inputFile, string outputFile, string additionalArguments)
        {
            var arguments = new StringBuilder();

            // Output path argument
            arguments.Append($"-d \"{this.outputPath}\" ");

            // Additional compiler arguments
            arguments.Append(additionalArguments);
            arguments.Append(' ');

            UnzipFile(inputFile, this.inputPath);

            // Input files arguments
            var filesToCompile = Directory.GetFiles(this.inputPath);
            for (var i = 0; i < filesToCompile.Length; i++)
            {
                arguments.Append($"\"{filesToCompile[i]}\"");
                arguments.Append(' ');
            }

            return arguments.ToString();
        }

        public override string ChangeOutputFileAfterCompilation(string outputFile)
        {
            var compiledFiles =
                Directory.GetFiles(this.outputPath, JavaCompiledFilesSearchPattern, SearchOption.AllDirectories);

            var destinationDirectory = new FileInfo(outputFile).Directory.ToString();

            compiledFiles.ForEach(file => File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), true));

            // TODO: Find the main class after analyzing which source file contains the main method
            var mainClassFile = compiledFiles
                .FirstOrDefault(file => file.EndsWith(MainClassFileNameSuffix, StringComparison.InvariantCultureIgnoreCase));

            return mainClassFile;
        }

        private static void UnzipFile(string fileToUnzip, string outputDirectory)
        {
            var fastZip = new FastZip();
            fastZip.ExtractZip(fileToUnzip, outputDirectory, null);
        }
    }
}
