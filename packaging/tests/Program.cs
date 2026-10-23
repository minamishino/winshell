using System.Text.Json;
using WinShell.App.Features.Mcp;

if (args is ["--publish-directory", var publishDirectory])
{
    var directory = Path.GetFullPath(publishDirectory);
    var configuration = McpClientLaunchConfiguration.Create(directory, directory);
    ExpectCommand(configuration, Path.Combine(directory, "Mcp", "WinShell.Mcp.exe"));
    Console.WriteLine("PASS published MCP configuration uses apphost with empty arguments");
    Console.WriteLine(configuration.Json);
    return 0;
}

var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var cases = new List<(string Name, Action Run)>();
var fixtureCount = 0;
const string selfContained = """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"}]}}""";
const string frameworkDependent = """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""";
var nativeFiles = new[] { "WinShell.Mcp.exe", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll" };

(string App, string Runtime, string Mcp) Fixture(string runtimeConfig, bool nativeRuntime = true)
{
    var app = Path.Combine(root, (++fixtureCount).ToString(), "应用 目录");
    var mcp = Directory.CreateDirectory(Path.Combine(app, "Mcp")).FullName;
    var runtime = Directory.CreateDirectory(Path.Combine(app, "shared-runtime", "shared", "Microsoft.NETCore.App", "10.0.12")).FullName;
    File.WriteAllText(Path.Combine(app, "shared-runtime", "dotnet.exe"), "fixture");
    foreach (var file in new[] { "WinShell.Mcp.dll", "WinShell.Mcp.deps.json" }) File.WriteAllText(Path.Combine(mcp, file), "fixture");
    File.WriteAllText(Path.Combine(mcp, "WinShell.Mcp.runtimeconfig.json"), runtimeConfig);
    if (nativeRuntime) foreach (var file in nativeFiles) File.WriteAllText(Path.Combine(mcp, file), "fixture");
    return (app, runtime, mcp);
}

static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
static void ExpectCommand(McpClientLaunchConfiguration result, string command, params string[] arguments)
{
    Expect(result.IsAvailable, result.Status);
    using var json = JsonDocument.Parse(result.Json!);
    var server = json.RootElement.GetProperty("mcpServers").GetProperty("winshell");
    Expect(server.GetProperty("command").GetString() == command, "Wrong executable selected.");
    Expect(server.GetProperty("args").EnumerateArray().Select(item => item.GetString()).SequenceEqual(arguments), "Wrong launch arguments.");
}

cases.Add(("Self-contained runtime accepts non-shared App runtime directory", () => {
    var f = Fixture(selfContained);
    ExpectCommand(McpClientLaunchConfiguration.Create(f.App, f.App), Path.Combine(f.Mcp, "WinShell.Mcp.exe"));
}));
cases.Add(("Self-contained apphost has priority over shared dotnet", () => {
    var f = Fixture(selfContained);
    ExpectCommand(McpClientLaunchConfiguration.Create(f.App, f.Runtime), Path.Combine(f.Mcp, "WinShell.Mcp.exe"));
}));
foreach (var missing in nativeFiles.Concat(new[] { "WinShell.Mcp.dll", "WinShell.Mcp.runtimeconfig.json", "WinShell.Mcp.deps.json" }))
    cases.Add(($"Incomplete self-contained deployment rejects missing {missing}", () => {
        var f = Fixture(selfContained);
        File.Delete(Path.Combine(f.Mcp, missing));
        Expect(!McpClientLaunchConfiguration.Create(f.App, f.Runtime).IsAvailable, "Incomplete self-contained payload must not emit a launch command.");
    }));
cases.Add(("Framework-dependent apphost still uses shared dotnet", () => {
    var f = Fixture(frameworkDependent);
    ExpectCommand(McpClientLaunchConfiguration.Create(f.App, f.Runtime), Path.Combine(f.App, "shared-runtime", "dotnet.exe"), Path.Combine(f.Mcp, "WinShell.Mcp.dll"));
}));
cases.Add(("Framework-dependent DLL retains shared dotnet fallback", () => {
    var f = Fixture(frameworkDependent, false);
    ExpectCommand(McpClientLaunchConfiguration.Create(f.App, f.Runtime), Path.Combine(f.App, "shared-runtime", "dotnet.exe"), Path.Combine(f.Mcp, "WinShell.Mcp.dll"));
}));
cases.Add(("Framework-dependent apphost cannot use a non-shared runtime", () => {
    var f = Fixture(frameworkDependent);
    Expect(!McpClientLaunchConfiguration.Create(f.App, f.App).IsAvailable, "Framework-dependent apphost was mistaken for self-contained.");
}));
cases.Add(("Shared fallback requires dotnet executable", () => {
    var f = Fixture(frameworkDependent, false);
    File.Delete(Path.Combine(f.App, "shared-runtime", "dotnet.exe"));
    Expect(!McpClientLaunchConfiguration.Create(f.App, f.Runtime).IsAvailable, "Missing dotnet must be rejected.");
}));
cases.Add(("Invalid and over-budget paths are rejected", () => {
    foreach (var path in new[] { "relative", "C:\\invalid\"path", "C:\\invalid\npath", "C:\\" + new string('a', 4096), "C:\\" + new string('中', 1400) })
        Expect(!McpClientLaunchConfiguration.Create(path, root).IsAvailable, "Invalid path was accepted.");
}));
foreach (var malformed in new[] { "not-json", "[]", "{}", "{\"runtimeOptions\":[]}", new string(' ', 32769) })
    cases.Add(($"Invalid runtime configuration rejected ({malformed.Length} bytes)", () => {
        var f = Fixture(malformed);
        Expect(!McpClientLaunchConfiguration.Create(f.App, f.Runtime).IsAvailable, "Invalid runtime configuration must not emit a launch command.");
    }));

var failures = 0;
foreach (var test in cases)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures++; Console.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
Console.WriteLine($"RESULT total={cases.Count} passed={cases.Count - failures} failed={failures}");
return failures == 0 ? 0 : 1;
