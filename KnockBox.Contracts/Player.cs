namespace KnockBox.Contracts;

/// <summary>A participant in a lobby. Identity is client-generated (no auth in the skeleton).</summary>
public sealed record Player(string Id, string DisplayName);
