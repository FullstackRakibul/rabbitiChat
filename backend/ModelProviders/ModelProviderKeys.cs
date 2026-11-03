// A provider's API key at rest: Data Protection, purpose "ModelProviders", key ring on the
// data volume (Program.cs). The plain key exists only in memory, on the way to a request.

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace RabbitIC.Api.ModelProviders;

public sealed class ModelProviderKeys(IDataProtectionProvider dataProtection)
{
    public const string Purpose = "ModelProviders";

    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);

    public string Protect(string apiKey) => _protector.Protect(apiKey);

    /// <summary>
    /// The stored key in plain text. False when it no longer decrypts — the key ring was lost
    /// or replaced — and the provider must be shown as "key unreadable", never crash.
    /// A null column (a keyless local server) is true with a null key.
    /// </summary>
    public bool TryUnprotect(string? stored, out string? apiKey)
    {
        apiKey = null;
        if (string.IsNullOrEmpty(stored)) return true;
        try
        {
            apiKey = _protector.Unprotect(stored);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The last 4 characters, or null for a key too short to show any of.</summary>
    public static string? Hint(string apiKey) => apiKey.Length >= 12 ? apiKey[^4..] : null;
}
