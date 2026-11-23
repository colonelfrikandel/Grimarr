using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Grimarr;

public sealed class OwnerSession
{
    private record State(string PasswordVersion,string Version);
    private readonly object gate=new();
    private readonly OwnerPassword password;
    private readonly IDataProtector protector;
    private readonly string file;
    public OwnerSession(IConfiguration configuration,IDataProtectionProvider protection):this(configuration,protection,new OwnerPassword(configuration)) { }
    public OwnerSession(IConfiguration configuration,IDataProtectionProvider protection,OwnerPassword password)
    {
        this.password=password;
        var root=configuration["GRIMARR_CONFIG"] ?? Path.Combine(AppContext.BaseDirectory,"config");AppSecurity.PrivateConfigDirectory(root);
        file=Path.Combine(root,"session-state");protector=protection.CreateProtector("Grimarr.OwnerSession.v1");
    }
    public string Version { get { lock(gate)return VersionFor(password.Current()); } }
    public string? LoginVersion(string? candidate)
    {
        if(candidate is null)return null;
        lock(gate)
        {
            var state=password.Current();if(!OwnerPassword.Verify(state,candidate))return null;
            return VersionFor(state);
        }
    }
    private string VersionFor(OwnerPassword.HashState passwordState)
    {
        var fingerprint=OwnerPassword.Fingerprint(passwordState);
        if(File.Exists(file))
        {
            var state=JsonSerializer.Deserialize<State>(protector.Unprotect(File.ReadAllText(file))) ?? throw new InvalidOperationException("Restore a valid owner session state from the complete config backup.");
            if(AppSecurity.ValidSession(state.PasswordVersion,fingerprint) && state.Version.Length==64)return state.Version;
        }
        var version=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var temporary=Path.Combine(Path.GetDirectoryName(file)!,".session-state-"+Guid.NewGuid().ToString("N"));
        try
        {
            using(var stream=new FileStream(temporary,OwnerPassword.PrivateOptions()))
            using(var writer=new StreamWriter(stream))writer.Write(protector.Protect(JsonSerializer.Serialize(new State(fingerprint,version))));
            File.Move(temporary,file,true);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
        return version;
    }
}
