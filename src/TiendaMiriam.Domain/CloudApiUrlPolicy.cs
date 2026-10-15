namespace TiendaMiriam.Domain;

public static class CloudApiUrlPolicy
{
    public static bool TryValidate(
        string? value,
        bool allowInsecureLoopback,
        out Uri? apiUri)
    {
        apiUri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        if (candidate.Scheme == Uri.UriSchemeHttps
            || candidate.Scheme == Uri.UriSchemeHttp && allowInsecureLoopback && candidate.IsLoopback)
        {
            apiUri = candidate;
            return true;
        }

        return false;
    }
}
