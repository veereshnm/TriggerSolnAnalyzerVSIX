using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CallGraphExtension
{
    internal sealed class CallGraphCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("b2c3d4e5-f678-9012-cdef-1234567890ab"); // Replace with your GUID
        private readonly AsyncPackage _package;

        private CallGraphCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Execute, menuCommandID)
            {
                Supported = false // Only enable for C# method context
            };
            menuItem.BeforeQueryStatus += BeforeQueryStatus;
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            new CallGraphCommand(package, commandService);
        }

        private void BeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var menuCommand = sender as OleMenuCommand;
            if (menuCommand == null) return;

            menuCommand.Enabled = false;
            var textManager = GetService(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null) return;

            if (textManager.GetActiveView(1, null, out var textView) != 0) return;

            var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte == null || dte.ActiveDocument == null) return;

            if (!dte.ActiveDocument.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

            var selection = (EnvDTE.TextSelection)dte.ActiveDocument.Selection;
            var position = selection.ActivePoint.AbsoluteCharOffset;

            using (var workspace = MSBuildWorkspace.Create())
            {
                var solution = workspace.OpenSolutionAsync(dte.Solution.FullName).Result;
                var document = GetDocumentAtPosition(solution, dte.ActiveDocument.FullName, position);
                if (document == null) return;

                var syntaxRoot = document.GetSyntaxRootAsync().Result;
                var node = syntaxRoot.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0));
                var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                menuCommand.Enabled = methodDecl != null;
            }
        }

        private Document GetDocumentAtPosition(Solution solution, string filePath, int position)
        {
            return solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        }

        private async void Execute(object sender, EventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);

            try
            {
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null || dte.ActiveDocument == null)
                {
                    await ShowMessageAsync("Error", "No active C# document found.");
                    return;
                }

                var solutionPath = dte.Solution.FullName;
                var documentPath = dte.ActiveDocument.FullName;
                var selection = (EnvDTE.TextSelection)dte.ActiveDocument.Selection;
                var position = selection.ActivePoint.AbsoluteCharOffset;

                using (var workspace = MSBuildWorkspace.Create())
                {
                    var solution = await workspace.OpenSolutionAsync(solutionPath);
                    var document = GetDocumentAtPosition(solution, documentPath, position);
                    if (document == null)
                    {
                        await ShowMessageAsync("Error", "Could not locate document in solution.");
                        return;
                    }

                    var syntaxRoot = await document.GetSyntaxRootAsync();
                    var node = syntaxRoot.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0));
                    var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                    if (methodDecl == null)
                    {
                        await ShowMessageAsync("Error", "No method found at cursor position.");
                        return;
                    }

                    var semanticModel = await document.GetSemanticModelAsync();
                    var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                    if (methodSymbol == null)
                    {
                        await ShowMessageAsync("Error", "Could not retrieve method symbol.");
                        return;
                    }

                    var methodName = methodSymbol.Name;
                    var classDecl = methodDecl.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                    var className = classDecl?.Identifier.Text ?? "UnknownClass";
                    var namespaceDecl = methodDecl.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
                    var namespaceName = namespaceDecl?.Name.ToString() ?? "UnknownNamespace";

                    var analyzerPath = @"C:\Tools\SolutionAnalyzer.exe"; // Adjust path as needed
                    var outputPath = Path.Combine(Path.GetDirectoryName(analyzerPath), "callchain.json");
                    var arguments = $"\"{methodName}\" \"{className}\" \"{namespaceName}\" \"{solutionPath}\"";

                    var processInfo = new ProcessStartInfo
                    {
                        FileName = analyzerPath,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(processInfo))
                    {
                        process.WaitForExit(); // .NET Framework 4.7.2 does not have WaitForExitAsync
                        if (process.ExitCode != 0)
                        {
                            var error = process.StandardError.ReadToEnd();
                            await ShowMessageAsync("Error", $"SolutionAnalyzer.exe failed: {error}");
                            return;
                        }
                    }

                    var angularAssetsPath = @"C:\Projects\call-graph-app\src\assets\callchain.json";
                    try
                    {
                        File.Copy(outputPath, angularAssetsPath, true);
                    }
                    catch (Exception ex)
                    {
                        await ShowMessageAsync("Error", $"Failed to copy callchain.json: {ex.Message}");
                        return;
                    }

                    var ngServeInfo = new ProcessStartInfo
                    {
                        FileName= "cmd.exe",
                        Arguments = "/C cd /d C:\\Projects\\call-graph-app && ng serve",
                        UseShellExecute = true,
                        CreateNoWindow = false
                    };

                    using (var ngProcess = Process.Start(ngServeInfo))
                    {
                        // Optionally wait or monitor ng serve process
                    }

                    await ShowMessageAsync("Success", "Call graph generated and Angular app started.");
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Error", $"An error occurred: {ex.Message}");
            }
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
            VsShellUtilities.ShowMessageBox(
                _package,
                message,
                title,
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private object GetService(Type serviceType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _package.GetService(serviceType);
        }
    }
}
