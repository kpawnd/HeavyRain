namespace HeavyRain.Core;

/// <summary>A Faronics driver or service and what Windows reports about it.</summary>
/// <param name="State">"Running", "Stopped" and so on, or null when Windows has no such driver or service.</param>
public sealed record ComponentStatus(string Name, string Role, bool IsDriver, string? State, string? StartMode)
{
    public bool Installed => State is not null;
    public bool Running => State?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record ComponentExpectation(string Name, string Role, bool IsDriver, string Product);

/// <summary>
/// Which Faronics components are present and running. The expected names are the ones registered
/// on a Deep Freeze 9.00.520.5760 Cloud workstation with Anti-Executable 6.11.2112.730.
/// </summary>
public static class ComponentHealth
{
    public static IReadOnlyList<ComponentExpectation> Expected { get; } =
    [
        new("DeepFrz", "Deep Freeze driver", true, "Deep Freeze"),
        new("DfDiskLo", "Deep Freeze disk driver", true, "Deep Freeze"),
        new("FarSpace", "Faronics storage driver", true, "Deep Freeze"),
        new("DFServ", "Deep Freeze service", false, "Deep Freeze"),
        new("FWASvc", "Faronics Cloud agent", false, "Cloud agent"),
        new("AEEngine", "Anti-Executable engine", false, "Anti-Executable")
    ];

    /// <summary>
    /// One short line per problem, or none when every expected component of an installed product
    /// is running. A product with none of its components registered counts as not installed and
    /// isn't reported as broken.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<ComponentStatus> components)
    {
        var problems = new List<string>();
        foreach (var product in Expected.GroupBy(expected => expected.Product))
        {
            var found = product.Select(expected => components.FirstOrDefault(c => c.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (found.All(component => component is null || !component.Installed)) continue;
            foreach (var (expected, component) in product.Zip(found))
            {
                if (component is null || !component.Installed) problems.Add($"{expected.Role} ({expected.Name}) isn't registered");
                else if (!component.Running) problems.Add($"{expected.Role} ({expected.Name}) is {component.State!.ToLowerInvariant()}");
            }
        }
        return problems;
    }

    public static string Summary(IReadOnlyList<ComponentStatus> components)
    {
        if (components.All(component => !component.Installed)) return "No Faronics drivers or services are registered.";
        var problems = Problems(components);
        return problems.Count == 0 ? "All Faronics components are running." : string.Join("\n", problems);
    }
}
