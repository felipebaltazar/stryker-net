using System.Collections.Generic;
using System.IO;
using System.Linq;
using Buildalyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;
using Stryker.Core.Exceptions;
using Stryker.Core.Initialisation.Buildalyzer;
using Stryker.Core.Logging;
using Stryker.Core.MutationTest;
using Stryker.Core.ToolHelpers;

namespace Stryker.Core.Compiling
{
    public interface ICompilingProcess
    {
        CompilingProcessResult Compile(IEnumerable<SyntaxTree> syntaxTrees, Stream ilStream, Stream symbolStream, bool devMode);
    }

    /// <summary>
    /// This process is in control of compiling the assembly and rolling back mutations that cannot compile
    /// Compiles the given input onto the memory stream
    public class CsharpCompilingProcess : ICompilingProcess
    {
        private readonly MutationTestInput _input;
        private readonly IRollbackProcess _rollbackProcess;
        private readonly INugetHelper _nugetHelper;
        private readonly ILogger _logger;

        public CsharpCompilingProcess(MutationTestInput input,
            IRollbackProcess rollbackProcess,
            INugetHelper nugetHelper)
        {
            _input = input;
            _rollbackProcess = rollbackProcess;
            _logger = ApplicationLogging.LoggerFactory.CreateLogger<CsharpCompilingProcess>();
            _nugetHelper = nugetHelper;
        }

        private string AssemblyName =>
            _input.ProjectInfo.ProjectUnderTestAnalyzerResult.GetAssemblyName();

        /// <summary>
        /// Compiles the given input onto the memory stream
        /// The compiling process is closely related to the rollback process. When the initial compilation fails, the rollback process will be executed.
        /// <param name="syntaxTrees">The syntax trees to compile</param>
        /// <param name="ilStream">The memory stream to store the compilation result onto</param>
        /// <param name="symbolStream">The memory stream to store the debug symbol</param>
        /// <param name="devMode">set to true to activate devmode (provides more information in case of internal failure)</param>
        /// </summary>
        public CompilingProcessResult Compile(IEnumerable<SyntaxTree> syntaxTrees, Stream ilStream, Stream symbolStream, bool devMode)
        {
            var analyzerResult = _input.ProjectInfo.ProjectUnderTestAnalyzerResult;
            var trees = syntaxTrees.ToList();
            var compilationOptions = analyzerResult.GetCompilationOptions();

            var compilation = CSharpCompilation.Create(AssemblyName,
                syntaxTrees: trees,
                options: compilationOptions,
                references: _input.AssemblyReferences);
            RollbackProcessResult rollbackProcessResult;

            // C# source generators must be executed before compilation
            compilation = RunSourceGenerators(analyzerResult, compilation);

            // first try compiling
            EmitResult emitResult;
            var retryCount = 1;
            (rollbackProcessResult, emitResult, retryCount) = TryCompilation(ilStream, symbolStream, compilation, null, false, devMode, retryCount);

            // If compiling failed and the error has no location, log and throw exception.
            if (!emitResult.Success && emitResult.Diagnostics.Any(diagnostic => diagnostic.Location == Location.None && diagnostic.Severity == DiagnosticSeverity.Error))
            {
                _logger.LogError("Failed to build the mutated assembly due to unrecoverable error: {0}",
                    emitResult.Diagnostics.First(diagnostic => diagnostic.Location == Location.None && diagnostic.Severity == DiagnosticSeverity.Error));
                throw new CompilationException("General Build Failure detected.");
            }

            const int maxAttempt = 50;
            for (var count = 1; !emitResult.Success && count < maxAttempt; count++)
            {
                // compilation did not succeed. let's compile a couple times more for good measure
                (rollbackProcessResult, emitResult, retryCount) = TryCompilation(ilStream, symbolStream, rollbackProcessResult?.Compilation ?? compilation, emitResult, retryCount == maxAttempt - 1, devMode, retryCount);
            }

            if (emitResult.Success)
            {
                return new CompilingProcessResult()
                {
                    Success = emitResult.Success,
                    RollbackResult = rollbackProcessResult
                };
            }
            // compiling failed
            _logger.LogError("Failed to restore the project to a buildable state. Please report the issue. Stryker can not proceed further");
            foreach (var emitResultDiagnostic in emitResult.Diagnostics)
            {
                _logger.LogWarning($"{emitResultDiagnostic}");
            }
            throw new CompilationException("Failed to restore build able state.");
        }

        private CSharpCompilation RunSourceGenerators(IAnalyzerResult analyzerResult, CSharpCompilation compilation)
        {
            var generators = analyzerResult.GetSourceGenerators(_logger);
            _ = CSharpGeneratorDriver
                .Create(generators)
                .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

            var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Location == Location.None);
            if (errors.Any())
            {
                foreach (var diagnostic in errors)
                {
                    _logger.LogError("Failed to generate source code for mutated assembly: {0}", diagnostic);
                }
                throw new CompilationException("Source Generator Failure");
            }
            return outputCompilation as CSharpCompilation;
        }

        private (RollbackProcessResult, EmitResult, int) TryCompilation(
            Stream ms,
            Stream symbolStream,
            CSharpCompilation compilation,
            EmitResult previousEmitResult,
            bool lastAttempt,
            bool devMode,
            int retryCount)
        {
            RollbackProcessResult rollbackProcessResult = null;

            if (previousEmitResult != null)
            {
                // remove broken mutations
                rollbackProcessResult = _rollbackProcess.Start(compilation, previousEmitResult.Diagnostics, lastAttempt, devMode);
                compilation = rollbackProcessResult.Compilation;
            }

            // reset the memoryStream
            ms.SetLength(0);
            symbolStream?.SetLength(0);

            _logger.LogDebug($"Trying compilation for the {ReadableNumber(retryCount)} time.");

            var emitOptions = symbolStream == null ? null : new EmitOptions(false, DebugInformationFormat.PortablePdb,
                _input.ProjectInfo.ProjectUnderTestAnalyzerResult.GetSymbolFileName());
            var emitResult = compilation.Emit(
                ms,
                symbolStream,
                manifestResources: _input.ProjectInfo.ProjectUnderTestAnalyzerResult.GetResources(_nugetHelper, _logger),
                win32Resources: compilation.CreateDefaultWin32Resources(
                    true, // Important!
                    false,
                    null,
                    null),
                options: emitOptions);

            LogEmitResult(emitResult);

            return (rollbackProcessResult, emitResult, ++retryCount);
        }

        private void LogEmitResult(EmitResult result)
        {
            if (!result.Success)
            {
                _logger.LogDebug("Compilation failed");

                foreach (var err in result.Diagnostics.Where(x => x.Severity is DiagnosticSeverity.Error))
                {
                    _logger.LogDebug("{0}, {1}", err?.GetMessage() ?? "No message", err?.Location.SourceTree?.FilePath ?? "Unknown filepath");
                }
            }
            else
            {
                _logger.LogDebug("Compilation successful");
            }
        }

        private static string ReadableNumber(int number)
        {
            return number switch
            {
                1 => "first",
                2 => "second",
                3 => "third",
                _ => (number + "th")
            };
        }
    }
}
