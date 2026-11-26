using System.Text.Json.Serialization;
using Hopkey.Core.Exclusion;
using Hopkey.Core.History;

namespace Hopkey.Core.Config;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(HistoryData))]
[JsonSerializable(typeof(List<ExclusionRule>))]
[JsonSerializable(typeof(UserDataPackage))]
internal sealed partial class JsonContext : JsonSerializerContext
{
}
