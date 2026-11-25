using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace CodeAssist.Security;

public sealed class ProtectedMcpTokenCache : ITokenCache
{
    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProtectedMcpTokenCache(string directory, string identity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The default persistent MCP cache requires Windows user-scoped data protection.");
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("codeassist:mcp:credential:" + identity));
        _path = Path.Combine(Path.GetFullPath(directory), Convert.ToHexString(_entropy) + ".bin");
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            if (plain.Length > 65_536) throw new IOException("MCP credentials exceed the cache budget.");
            var protectedBytes = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, _path, overwrite: true);
                }
                finally { File.Delete(temporary); }
            }
            finally { _gate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return null;
            if (new FileInfo(_path).Length > 131_072) throw new IOException("MCP credential cache exceeds its budget.");
            var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken);
            var plain = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<TokenContainer>(plain) ?? throw new IOException("MCP credential cache is invalid."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { _gate.Release(); }
    }

    public async Task ForgetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { File.Delete(_path); }
        finally { _gate.Release(); }
    }
}