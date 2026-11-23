using System.Security.Cryptography;
using System.Text.Json;

namespace Grimarr;

public sealed class OwnerPassword
{
    public sealed record HashState(string Algorithm,int Iterations,string Salt,string Hash);
    public const int Iterations=600_000;
    private readonly string file;
    public OwnerPassword(IConfiguration configuration)
    {
        var root=configuration["GRIMARR_CONFIG"] ?? Path.Combine(AppContext.BaseDirectory,"config");
        AppSecurity.PrivateConfigDirectory(root);file=Path.Combine(root,"owner-password.json");
        if(!File.Exists(file))
        {
            var initial=configuration["GRIMARR_PASSWORD"];
            if(initial is null)throw new InvalidOperationException("Initialize the owner password with: dotnet Grimarr.dll password. Include owner-password.json in complete config backups.");
            Write(initial,false);
        }
        _=Current();
    }
    public HashState Current()
    {
        var state=JsonSerializer.Deserialize<HashState>(File.ReadAllText(file)) ?? throw new InvalidOperationException("Restore a valid owner password hash.");
        if(state.Algorithm!="PBKDF2-SHA256" || state.Iterations!=Iterations || Convert.FromBase64String(state.Salt).Length!=32 || Convert.FromBase64String(state.Hash).Length!=32)
            throw new InvalidOperationException("Restore a valid owner password hash.");
        return state;
    }
    public static string Fingerprint(HashState state)=>AppSecurity.PasswordFingerprint(JsonSerializer.Serialize(state));
    public static bool Verify(HashState state,string password)=>CryptographicOperations.FixedTimeEquals(
        Rfc2898DeriveBytes.Pbkdf2(password,Convert.FromBase64String(state.Salt),state.Iterations,HashAlgorithmName.SHA256,32),Convert.FromBase64String(state.Hash));
    public void Set(string password)=>Write(password,true);
    private void Write(string password,bool overwrite)=>WriteState(file,Create(password),overwrite);
    private static HashState Create(string password)
    {
        var salt=RandomNumberGenerator.GetBytes(32);
        var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);
        return new("PBKDF2-SHA256",Iterations,Convert.ToBase64String(salt),Convert.ToBase64String(hash));
    }
    private static void WriteState(string file,HashState state,bool overwrite)
    {
        var temporary=Path.Combine(Path.GetDirectoryName(file)!,".owner-password-"+Guid.NewGuid().ToString("N"));
        try
        {
            using(var stream=new FileStream(temporary,PrivateOptions()))
            { JsonSerializer.Serialize(stream,state);stream.Flush(true); }
            File.Move(temporary,file,overwrite);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
    internal static FileStreamOptions PrivateOptions()
    {
        var options=new FileStreamOptions {Mode=FileMode.CreateNew,Access=FileAccess.Write};
        if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        return options;
    }
    public static int ChangeFromConsole(string[] arguments)
    {
        try
        {
            string password,confirmation;
            if(arguments.SequenceEqual(new[]{"--stdin"}))
            {
                using var json=JsonDocument.Parse(Console.In.ReadToEnd());
                password=json.RootElement.GetProperty("password").GetString() ?? throw new InvalidOperationException();
                confirmation=json.RootElement.GetProperty("confirmation").GetString() ?? throw new InvalidOperationException();
            }
            else
            {
                if(arguments.Length!=0 || Console.IsInputRedirected)throw new InvalidOperationException();
                password=ReadHidden("New password: ");confirmation=ReadHidden("Confirm password: ");
            }
            if(password!=confirmation)throw new InvalidOperationException();
            var configuration=new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var root=configuration["GRIMARR_CONFIG"] ?? Path.Combine(AppContext.BaseDirectory,"config");AppSecurity.PrivateConfigDirectory(root);
            WriteState(Path.Combine(root,"owner-password.json"),Create(password),true);
            Console.WriteLine("Password updated. Only its salted hash was saved. Sign in again; existing sessions are revoked on their next request. No restart is needed.");return 0;
        }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        { Console.Error.WriteLine("Password was not changed. Check confirmation, config permissions and command input. Use an interactive terminal, or --stdin with JSON password and confirmation.");return 1; }
    }
    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);var characters=new List<char>();
        while(true)
        {
            var key=Console.ReadKey(true);
            if(key.Key==ConsoleKey.Enter){Console.WriteLine();return new string(characters.ToArray());}
            if(key.Key==ConsoleKey.Backspace){if(characters.Count>0){var last=characters[^1];characters.RemoveAt(characters.Count-1);if(char.IsLowSurrogate(last)&&characters.Count>0&&char.IsHighSurrogate(characters[^1]))characters.RemoveAt(characters.Count-1);}continue;}
            if(key.KeyChar!='\0')characters.Add(key.KeyChar);
        }
    }
}
