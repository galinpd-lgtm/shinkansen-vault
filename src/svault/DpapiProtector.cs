using System.Security.Cryptography;
using System.Text;

namespace ShinkansenVault.Cli;

/// <summary>DPAPI, CurrentUser scope: the wrapped key can only be unwrapped by this Windows user on this machine.</summary>
internal sealed class DpapiProtector : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ShinkansenVault/DPAPI/v1");

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
