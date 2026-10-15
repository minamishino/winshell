using WinShell.Core.Docker;
using System.Text.Json;

namespace WinShell.Infrastructure.Ssh.Docker;

public static class DockerMetadataReader
{
    public static IReadOnlyList<DockerContainerSummary> Read(string output)
    {
        var lineNumber = 0;
        var reason = "输出超过 32768 UTF-8 字节，或包含 NUL / 无效 Unicode";
        try
        {
            DockerScriptBuilder.ValidateText(output, 32768);
            List<DockerContainerSummary> containers = [];
            HashSet<string> identifiers = new(StringComparer.Ordinal);
            using var lines = new StringReader(output);
            while (lines.ReadLine() is { } line)
            {
                lineNumber++;
                if (line.Length == 0) continue;
                reason = "容器数超过 32";
                if (containers.Count == 32) throw new JsonException("容器数超过 32。");
                reason = "需要合法 JSON 对象，请检查 SSH 启动脚本是否向标准输出写入欢迎语";
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("需要 JSON 对象。");
                Dictionary<string, string> fields = new(StringComparer.Ordinal);
                foreach (var field in document.RootElement.EnumerateObject())
                {
                    // Only fixed schema names enter the diagnostic; remote values never do.
                    var fieldLabel = field.Name is "ID" or "Names" or "Image" or "State" or "Status" or "Ports" ? field.Name : "扩展字段";
                    reason = $"{fieldLabel} 重复、类型不是字符串，或字段数量 / 名称长度超过限制";
                    if (fields.Count == 32 || field.Name.Length > 64 || field.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(field.Name, field.Value.GetString()!))
                        throw new JsonException("重复字段、类型或字段数量无效。");
                    reason = $"{fieldLabel} 超过 1024 UTF-8 字节，或包含 NUL / 无效 Unicode";
                    DockerScriptBuilder.ValidateText(fields[field.Name], 1024);
                }
                string Required(string name)
                {
                    reason = "缺少字段 " + name;
                    return fields.TryGetValue(name, out var value) ? value : throw new JsonException(reason);
                }
                var identifier = Required("ID");
                reason = "ID 必须为完整的 64 位小写十六进制标识";
                DockerScriptBuilder.Identifier(identifier);
                reason = "容器 ID 重复";
                if (!identifiers.Add(identifier)) throw new JsonException("重复容器 ID。");
                containers.Add(new(identifier, Required("Names"), Required("Image"), Required("State"), Required("Status"), Required("Ports")));
            }
            return containers.AsReadOnly();
        }
        catch (Exception exception) when (exception is JsonException or DockerException)
        {
            var location = lineNumber == 0 ? "输出" : $"第 {lineNumber} 行";
            throw new DockerException(DockerFailureKind.InvalidMetadata, $"Docker 列表{location}无效：{reason}；未显示不完整结果。", exception);
        }
    }
}
