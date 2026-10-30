using System.Collections.Concurrent;
using System.Reflection;

namespace Writ.Runtime;

/// <summary>Marks a static Machine helper as the implementation of a catalog tool id.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WritToolAttribute : Attribute
{
    public string Name { get; }
    public bool Writes { get; init; }

    public WritToolAttribute(string name) => Name = name;
}

/// <summary>
/// Maps catalog tool ids to in-process handlers. Built once from <see cref="WritToolAttribute"/>
/// methods on <see cref="Machine"/> (and optional manual registrations).
/// </summary>
public static class ToolRegistry
{
    private static readonly ConcurrentDictionary<string, Func<Dictionary<string, object?>, object?>> Handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, bool> WriteTools =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _loaded;

    public static IReadOnlyCollection<string> RegisteredNames
    {
        get
        {
            EnsureLoaded();
            return Handlers.Keys.ToList();
        }
    }

    public static bool IsWrite(string name)
    {
        EnsureLoaded();
        return WriteTools.ContainsKey(name);
    }

    public static bool IsRegistered(string name)
    {
        EnsureLoaded();
        return Handlers.ContainsKey(name);
    }

    public static void Register(string name, Func<Dictionary<string, object?>, object?> handler, bool writes = false)
    {
        Handlers[name] = handler;
        if (writes)
        {
            WriteTools[name] = true;
        }
        else
        {
            WriteTools.TryRemove(name, out _);
        }
    }

    public static bool TryInvoke(string name, Dictionary<string, object?> arguments, out object? result)
    {
        EnsureLoaded();
        if (!Handlers.TryGetValue(name, out var handler))
        {
            result = null;
            return false;
        }

        result = handler(arguments);
        return true;
    }

    private static void EnsureLoaded()
    {
        if (Interlocked.CompareExchange(ref _loaded, 1, 0) != 0)
        {
            return;
        }

        foreach (var method in typeof(Machine).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            foreach (var attribute in method.GetCustomAttributes<WritToolAttribute>())
            {
                if (method.GetParameters() is not [{ ParameterType: var parameterType }] ||
                    parameterType != typeof(Dictionary<string, object?>))
                {
                    throw new InvalidOperationException(
                        $"WritTool '{attribute.Name}' on {method.Name} must take Dictionary<string, object?>.");
                }

                var captured = method;
                Register(attribute.Name, args => captured.Invoke(null, [args]), attribute.Writes);
            }
        }

        Machine.RegisterBuiltinTools();
    }
}
