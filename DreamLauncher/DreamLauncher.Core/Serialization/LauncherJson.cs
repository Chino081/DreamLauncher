using System.Text.Json;
using System.Text.Json.Serialization;

namespace DreamLauncher.Core.Serialization;

public static class LauncherJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
