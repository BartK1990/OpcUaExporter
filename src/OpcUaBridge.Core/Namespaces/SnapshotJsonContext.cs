using System.Text.Json.Serialization;

namespace OpcUaBridge.Namespaces;

/// <summary>
/// Source-generated serialization for the snapshot.
/// </summary>
/// <remarks>
/// Worth the attribute: at a few thousand tags the reflection-based serializer's start-up
/// cost is a noticeable part of how long the service takes to begin serving after a reboot.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NamespaceSnapshot))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext;
