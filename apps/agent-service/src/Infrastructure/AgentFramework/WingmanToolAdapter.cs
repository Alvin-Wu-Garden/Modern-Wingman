using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.Extensions.AI;

namespace AgentService.Infrastructure.AgentFramework;

/// <summary>
/// Adapts the provider-neutral Wingman tool registry to standard MEAI functions.
/// Each registry entry is exposed as an individual function so providers can use
/// the descriptor's real JSON schema for tool selection and argument generation.
/// </summary>
public static partial class WingmanToolAdapter
{
    private const int MaximumFunctionNameLength = 64;

    public static IReadOnlyList<AIFunction> CreateTools(
        IToolRegistry registry,
        AgentCreationContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        // Take a stable snapshot. Marketplace reconciliation may replace plugin
        // tools while an existing agent invocation is still in progress.
        return registry.ListTools()
            .Select(descriptor => (AIFunction)new RegistryAIFunction(registry, descriptor, context))
            .ToList();
    }

    internal static string ToFunctionName(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (toolName.Length <= MaximumFunctionNameLength && ValidFunctionName().IsMatch(toolName))
            return toolName;

        var sanitized = InvalidFunctionNameCharacter().Replace(toolName, "_").Trim('_');
        if (sanitized.Length == 0)
            sanitized = "tool";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(toolName)))
            .ToLowerInvariant()[..8];
        const string prefix = "wingman_";
        var available = MaximumFunctionNameLength - prefix.Length - hash.Length - 1;
        if (sanitized.Length > available)
            sanitized = sanitized[..available];

        return $"{prefix}{sanitized}_{hash}";
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidFunctionName();

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidFunctionNameCharacter();

    private sealed class RegistryAIFunction : AIFunction
    {
        private readonly IToolRegistry _registry;
        private readonly ToolDescriptor _descriptor;
        private readonly AgentCreationContext _context;

        public RegistryAIFunction(
            IToolRegistry registry,
            ToolDescriptor descriptor,
            AgentCreationContext context)
        {
            _registry = registry;
            _descriptor = descriptor;
            _context = context;
            Name = ToFunctionName(descriptor.Name);
            Description = Name == descriptor.Name
                ? descriptor.Description
                : $"{descriptor.Description} (Wingman tool id: {descriptor.Name})";
            JsonSchema = ParseSchema(descriptor);
        }

        public override string Name { get; }

        public override string Description { get; }

        public override JsonElement JsonSchema { get; }

        public override JsonSerializerOptions JsonSerializerOptions => AIJsonUtilities.DefaultOptions;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var toolArguments = arguments.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);

            var result = await _registry.ExecuteAsync(
                new ToolExecutionRequest(
                    _descriptor.Name,
                    toolArguments,
                    new ToolExecutionContext(
                        _context.RunId ?? Guid.NewGuid().ToString("N"),
                        _context.Mode,
                        _context.WorkspacePath ?? Environment.CurrentDirectory,
                        _context.ProjectId)),
                cancellationToken);

            return JsonSerializer.SerializeToElement(result, JsonSerializerOptions);
        }

        private static JsonElement ParseSchema(ToolDescriptor descriptor)
        {
            try
            {
                using var document = JsonDocument.Parse(descriptor.InputSchemaJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"Tool '{descriptor.Name}' input schema must be a JSON object.");
                }

                return document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Tool '{descriptor.Name}' has an invalid JSON input schema.",
                    ex);
            }
        }
    }
}
