namespace OpenServiceBus.Explorer.Environments;

public sealed record ExplorerEnvironment(string Name, List<EnvironmentValue> Values);

public sealed record EnvironmentValue(string Key, string Value, bool Enabled = true);
