using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading.Tasks;
using CLAP;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;

namespace Faithlife.RunAnalyzers
{
	internal static class Program
	{
		private static async Task<int> Main(string[] args)
		{
			MSBuildLocator.RegisterDefaults();
			var commandLine = new CommandLine();

			Parser.Run(args, commandLine);

			return await (commandLine.Completion ?? Task.FromResult(0));
		}

		internal sealed class CommandLine
		{
			public Task<int> Completion { get; private set; }

			[Verb(IsDefault = true)]
			public void Run([Required] string solutionPath, [Required] string analyzerPath)
			{
				async Task<int> RunCore()
				{
					var assembly = TryLoadAssemblyFrom(analyzerPath);
					if (assembly == null)
						return 1;

					var analyzers = assembly
						.GetExportedTypes()
						.Where(x => x.IsSubclassOf(typeof(DiagnosticAnalyzer)))
						.Select(x => x.GetConstructor(Array.Empty<Type>()).Invoke(Array.Empty<object>()))
						.Cast<DiagnosticAnalyzer>()
						.ToImmutableArray();

					var workspace = CreateMsBuildWorkspaceWorkspace();

					var solution = await workspace.OpenSolutionAsync(solutionPath);
					var solutionDirectory = Path.GetDirectoryName(solutionPath);

					foreach (var project in solution.Projects)
					{
						var hasDisplayedProjectName = false;
						var projectDirectory = Path.GetDirectoryName(project.FilePath);

						var compilation = (await project.GetCompilationAsync()).WithAnalyzers(analyzers);

						var diagnostics = await compilation.GetAnalyzerDiagnosticsAsync();

						foreach (var diagnostic in diagnostics)
						{
							if (!hasDisplayedProjectName)
							{
								var projectPath = project.FilePath.StartsWith(solutionDirectory, StringComparison.Ordinal) ?
									project.FilePath.Substring(solutionDirectory.Length + 1) :
									project.FilePath;
								Console.WriteLine($"\nIn {projectPath}:\n");
								hasDisplayedProjectName = true;
							}

							var location = diagnostic.Location.GetMappedLineSpan();
							var filePath = location.Path.StartsWith(projectDirectory, StringComparison.Ordinal) ?
								location.Path.Substring(projectDirectory.Length + 1) :
								location.Path;

							Console.WriteLine($"{filePath}, line {location.StartLinePosition} {diagnostic.Id}: {diagnostic.GetMessage()}");
						}
					}

					return 0;
				}

				Completion = RunCore();
			}

			[Empty, Help]
			public void Usage(string help) => Console.WriteLine(help);
		}

		private static Assembly TryLoadAssemblyFrom(string path)
		{
			try
			{
				return Assembly.LoadFrom(path ?? throw new ArgumentNullException(nameof(path)));
			}
			catch (FileNotFoundException)
			{
				Console.Error.WriteLine($"No analyzer assembly found at {path}");
			}
			catch (FileLoadException)
			{
				Console.Error.WriteLine($"Analyzer assembly at {path} could not be loaded.");
			}
			catch (BadImageFormatException)
			{
				Console.Error.WriteLine($"Analyzer assembly at {path} could not be loaded.");
			}
			catch (SecurityException)
			{
				Console.Error.WriteLine($"Analyzer assembly at {path} could not be loaded.");
			}

			return null;
		}

		private static MSBuildWorkspace CreateMsBuildWorkspaceWorkspace()
		{
			// HACK: Some web projects contain a reference to Microsoft.WebApplications.targets
			// that causes loading to fail under MSBuild > 15.0. Setting VSToolsPath to the empty
			// string prevents MSBuild from trying to load the targets file.
			var msBuildOptions = ImmutableDictionary<string, string>.Empty.Add("VSToolsPath", "");

			var workspace = MSBuildWorkspace.Create(msBuildOptions);
			workspace.Options = workspace.Options.WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, true);
			workspace.WorkspaceFailed += Workspace_WorkspaceFailed;

			return workspace;
		}

		private static void Workspace_WorkspaceFailed(object sender, WorkspaceDiagnosticEventArgs e)
		{
		}
	}
}
