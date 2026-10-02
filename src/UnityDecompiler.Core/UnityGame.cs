namespace UnityDecompiler.Core;

public enum ScriptingBackend
{
    Unknown,
    Mono,
    IL2CPP,
}

/// <summary>Everything we learned about a Unity build by looking at its files.</summary>
public sealed class UnityGame
{
    public required string Name { get; init; }
    public required string RootDirectory { get; init; }
    public string? DataDirectory { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ManagedDirectory { get; init; }
    public string? UnityVersion { get; init; }
    public string? CompanyName { get; init; }
    public string? ProductName { get; init; }
    public string? Il2CppMetadataPath { get; init; }
    public string? GameAssemblyPath { get; init; }
    public ScriptingBackend Backend { get; init; }

    /// <summary>Loose .dll files given directly by the user (no full game folder).</summary>
    public IReadOnlyList<string> LooseAssemblies { get; init; } = Array.Empty<string>();

    public string DisplayName => string.IsNullOrWhiteSpace(ProductName) ? Name : ProductName!;
}
