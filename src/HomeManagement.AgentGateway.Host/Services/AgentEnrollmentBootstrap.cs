using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.AgentGateway.Host.Services;

internal static class AgentEnrollmentBootstrap
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static int? Run(string[] arguments, TextWriter output, TextWriter error)
    {
        if (!arguments.Any(arg => arg.StartsWith("--initialize-agent-enrollment", StringComparison.Ordinal)))
            return null;
        try
        {
            if (arguments.Length != 7 || arguments[0] != "--initialize-agent-enrollment")
                throw new ArgumentException("Invalid bootstrap arguments.");
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < arguments.Length; index += 2)
            {
                if (arguments[index] is not ("--directory" or "--wrapping-key-file" or "--assignment-file")
                    || string.IsNullOrWhiteSpace(arguments[index + 1]) || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                    || !paths.TryAdd(arguments[index], arguments[index + 1]))
                    throw new ArgumentException("Invalid bootstrap arguments.");
            }
            if (paths.Count != 3) throw new ArgumentException("Incomplete bootstrap paths.");
            using var input = File.OpenRead(paths["--assignment-file"]);
            if (input.Length is < 1 or > EncryptedAgentEnrollmentStore.MaximumStateBytes)
                throw new InvalidDataException("Invalid assignment manifest size.");
            var manifest = JsonSerializer.Deserialize<AssignmentManifest>(input, JsonOptions);
            if (manifest is not { SchemaVersion: 1, Assignments: not null } || manifest.Assignments.Any(a => a is null))
                throw new InvalidDataException("Invalid assignment manifest.");
            EncryptedAgentEnrollmentStore.Initialize(paths["--directory"], paths["--wrapping-key-file"], manifest.Assignments);
            output.WriteLine($"Initialized {manifest.Assignments.Count} explicit agent assignments. No service started.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or CryptographicException)
        {
            error.WriteLine("Agent enrollment initialization failed. Check arguments, manifest, empty directory, key and exclusive access.");
            return 1;
        }
    }

    private sealed record AssignmentManifest(int SchemaVersion, IReadOnlyList<AgentEnrollmentAssignment> Assignments);
}
