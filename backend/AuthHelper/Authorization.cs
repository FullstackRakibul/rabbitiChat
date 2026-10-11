using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using System.Security.Cryptography;
using System.Text;

namespace RabbitIC.Api.AuthHelper
{
    public class Authorization
    {
        public static async Task<User?> ResolveUser(HttpContext ctx, AppDbContext db)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
            var token = header["Bearer ".Length..].Trim();
            if (string.IsNullOrEmpty(token)) return null;
            return await db.Users.FirstOrDefaultAsync(u => u.Token == token);
        }


        public static(string hash, string salt) HashSecret(string secret, byte[]? existingSalt = null)
        {
            var salt = existingSalt ?? RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(secret), salt, 100_000, HashAlgorithmName.SHA256, 32);
            return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
        }

        /// <summary>
        /// Whether <paramref name="secret"/> is this user's secret: the same PBKDF2 hash
        /// with the user's own salt, compared in constant time. Login and the
        /// change-secret route both ask here, so there is one way to check a secret.
        /// </summary>
        public static bool VerifySecret(User user, string secret)
        {
            var (hash, _) = HashSecret(secret, Convert.FromBase64String(user.SecretSalt));
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(hash), Convert.FromBase64String(user.SecretHash));
        }

        public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";


    }
}
