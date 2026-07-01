using System;
using System.IO;
using System.Text.Json;

namespace LogicApp.Tests.Unit
{
    /// <summary>
    /// Loads workflow.json / parameters.json / connections.json from the Logic App project so the
    /// static structural tests can assert against them. Walks up from the test output directory to
    /// find the func project by its host.json. Change the folder name below if your Logic App
    /// project folder is not "LogicApp_Template".
    /// </summary>
    internal static class WorkflowFiles
    {
        private const string LogicAppFolderName = "LogicApp_Template";

        private static readonly Lazy<string> _logicAppRoot = new(FindLogicAppRoot);

        public static string LogicAppRoot => _logicAppRoot.Value;

        public static JsonDocument LoadWorkflow(string workflowName)
        {
            var path = Path.Combine(LogicAppRoot, workflowName, "workflow.json");
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        public static JsonElement LoadDefinition(string workflowName)
        {
            var doc = LoadWorkflow(workflowName);
            return doc.RootElement.GetProperty("definition").Clone();
        }

        public static JsonElement LoadParameters() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(LogicAppRoot, "parameters.json"))).RootElement.Clone();

        public static JsonElement LoadConnections() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(LogicAppRoot, "connections.json"))).RootElement.Clone();

        private static string FindLogicAppRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, LogicAppFolderName, "host.json");
                if (File.Exists(candidate)) return Path.Combine(dir.FullName, LogicAppFolderName);
                dir = dir.Parent!;
            }
            throw new InvalidOperationException(
                $"Could not locate {LogicAppFolderName}/host.json from " + AppContext.BaseDirectory);
        }
    }
}
