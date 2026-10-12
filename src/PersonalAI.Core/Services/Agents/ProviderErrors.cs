using System.Net.Http;

namespace PersonalAI.Core.Services.Agents;

internal static class ProviderErrors
{
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" -> ", parts);
    }

    public static bool IsTransient(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is HttpRequestException or IOException)
                return true;
        return false;
    }
}
