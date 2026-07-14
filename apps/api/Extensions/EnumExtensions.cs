using System.Text.Json;

namespace MigrationPlatform.Api.Extensions;

public static class EnumExtensions
{
    public static string ToCamelCase(this Enum value)
        => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
