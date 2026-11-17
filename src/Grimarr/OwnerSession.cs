using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Grimarr;

public sealed class OwnerSession
{
    private record State(string PasswordVersion,string Version);
    public string Version { get; }
    public OwnerSession(IConfiguration configuration,IDataProtectionProvider protection)
    {
        var password=configuration["GRIMARR_PASSWORD"] ?? throw new InvalidOperationException("Configure the owner password.");
        var root=configuration["GRIMARR_CONFIG"] ?? Path.Combine(AppContext.BaseDirectory,"config");AppSecurity.PrivateConfigDirectory(root);
        var file=Path.Combine(root,"session-state");var protector=protection.CreateProtector("Grimarr.OwnerSession.v1");
        var fingerprint=AppSecurity.PasswordFingerprint(password);
        if(File.Exists(file))
        {
            var state=JsonSerializer.Deserialize<State>(protector.Unprotect(File.ReadAllText(file))) ?? throw new InvalidOperationException("Restore a valid owner session state from the complete config backup.");
            if(AppSecurity.ValidSession(state.PasswordVersion,fingerprint) && state.Version.Length==64) { Version=state.Version;return; }
        }
        Version=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var temporary=Path.Combine(root,".session-state-"+Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temporary,protector.Protect(JsonSerializer.Serialize(new State(fingerprint,Version))));
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            File.Move(temporary,file,true);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
}
