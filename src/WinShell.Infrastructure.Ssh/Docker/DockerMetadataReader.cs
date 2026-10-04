using WinShell.Core.Docker;
using System.Text.Json;

namespace WinShell.Infrastructure.Ssh.Docker;

public static class DockerMetadataReader
{
    public static IReadOnlyList<DockerContainerSummary> Read(string output)
    {
        try
        {
            DockerScriptBuilder.ValidateText(output, 32768);
            List<DockerContainerSummary> containers = [];
            HashSet<string> identifiers = new(StringComparer.Ordinal);
            using var lines = new StringReader(output);
            while (lines.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (containers.Count == 32) throw new JsonException("容器数超过 32。");
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("需要 JSON 对象。");
                Dictionary<string, string> fields = new(StringComparer.Ordinal);
                foreach (var field in document.RootElement.EnumerateObject())
                {
                    if (fields.Count == 32 || field.Name.Length > 64 || field.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(field.Name, field.Value.GetString()!))
                        throw new JsonException("重复字段、类型或字段数量无效。");
                    DockerScriptBuilder.ValidateText(fields[field.Name], 1024);
                }
                string Required(string name) => fields.TryGetValue(name, out var value) ? value : throw new JsonException("缺少字段：" + name);
                var identifier = Required("ID");
                DockerScriptBuilder.Identifier(identifier);
                if (!identifiers.Add(identifier)) throw new JsonException("重复容器 ID。");
                containers.Add(new(identifier, Required("Names"), Required("Image"), Required("State"), Required("Status"), Required("Ports")));
            }
            return containers.AsReadOnly();
        }
        catch (Exception exception) when (exception is JsonException or DockerException)
        { throw new DockerException(DockerFailureKind.InvalidMetadata, "Docker 列表元数据无效或超过限制；未显示不完整结果。", exception); }
    }
}
