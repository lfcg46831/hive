using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

internal static class CurrentExperimentManifest
{
    public static void Write(
        string sourceManifestPath,
        string targetManifestPath,
        string repositoryRoot)
    {
        var root = JsonNode.Parse(File.ReadAllText(sourceManifestPath))?.AsObject()
            ?? throw new InvalidDataException("Experiment manifest fixture is empty.");
        Update(root["organization"]!["configuration"]!.AsObject(), repositoryRoot);
        Update(root["evaluation"]!["corpus"]!.AsObject(), repositoryRoot);
        Update(root["evaluation"]!["rubric"]!.AsObject(), repositoryRoot);
        foreach (var item in root["reproducibility"]!.AsArray())
        {
            Update(item!.AsObject(), repositoryRoot);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetManifestPath)!);
        File.WriteAllText(
            targetManifestPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static void Update(JsonObject reference, string repositoryRoot)
    {
        var relativePath = reference["path"]!.GetValue<string>();
        var fullPath = Path.Combine(
            repositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        reference["sha256"] = EvaluationArtifactIndex.FileSha256(fullPath);
    }
}
