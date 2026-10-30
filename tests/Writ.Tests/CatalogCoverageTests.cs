using Writ.Compiler.Capabilities;
using Writ.Runtime;

namespace Writ.Tests;

public class CatalogCoverageTests
{
    [Fact]
    public void Every_catalog_tool_has_a_registered_Machine_handler()
    {
        var missing = CmdletCatalog.Default.Cmdlets
            .Select(entry => entry.Name)
            .Where(name => !ToolRegistry.IsRegistered(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Catalog tools without Machine handlers:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Merged_catalog_includes_powershell_and_writ_tools()
    {
        Assert.Contains(CmdletCatalog.Default.Cmdlets, entry => entry.Name == "Get-ChildItem");
        Assert.Contains(CmdletCatalog.Default.Cmdlets, entry => entry.Name == "text.trim");
        Assert.Contains(CmdletCatalog.Default.Cmdlets, entry => entry.Name == "secret.get");
        Assert.Contains(CmdletCatalog.Default.Cmdlets, entry => entry.Name == "ai.infer");
    }
}
