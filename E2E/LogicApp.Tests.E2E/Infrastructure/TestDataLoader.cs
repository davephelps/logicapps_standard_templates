// TestDataLoader -- read JSON / binary fixtures from __testdata/ at runtime.

using System.Text.Json;

namespace LogicApp.Tests.E2E.Infrastructure;

public static class TestDataLoader
{
    public static string ReadText(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        return File.ReadAllText(fullPath);
    }

    public static byte[] ReadBytes(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        return File.ReadAllBytes(fullPath);
    }

    public static T ReadJson<T>(string relativePath)
    {
        var json = ReadText(relativePath);
        var parsed = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return parsed ?? throw new InvalidDataException(
            $"Could not deserialize __testdata file '{relativePath}' as {typeof(T).Name}.");
    }

    private static string ResolvePath(string relativePath)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (File.Exists(candidate)) return candidate;

        // Allow callers to omit the __testdata/ prefix.
        var prefixed = Path.Combine(AppContext.BaseDirectory, "__testdata", relativePath);
        if (File.Exists(prefixed)) return prefixed;

        throw new FileNotFoundException(
            $"Test data file not found at '{candidate}' or '{prefixed}'.", relativePath);
    }
}
