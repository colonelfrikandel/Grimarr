using System.Security.Cryptography;
using System.Text;

namespace Grimarr;

public static class AppSecurity
{
    public const int MaximumApiBody=64*1024;
    public const int MaximumQuickAddBody=24*1024*1024;
    public const string SessionClaim="grimarr.owner-session";
    public static string PasswordFingerprint(string password)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Grimarr.Session.v1\0"+password)));
    public static bool ValidSession(string? actual,string expected)=>actual?.Length==expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual),Encoding.UTF8.GetBytes(expected));
    public static void PrivateConfigDirectory(string path)
    {
        // Existing operator-managed permissions are retained; new config roots are owner-only.
        if(OperatingSystem.IsWindows())Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }
}
