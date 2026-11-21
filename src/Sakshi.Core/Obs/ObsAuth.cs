using System.Security.Cryptography;
using System.Text;

namespace Sakshi.Core.Obs;

public static class ObsAuth
{
    /// <summary>obs-websocket v5: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    public static string Response(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }
}
