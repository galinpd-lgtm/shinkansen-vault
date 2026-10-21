using System.Security.Cryptography;
using System.Text;

namespace ShinkansenVault;

/// <summary>AES-256-GCM sealing and HKDF key derivation. Sealed format: nonce(12) | ciphertext | tag(16).</summary>
public static class VaultCrypto
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    private static readonly byte[] KekInfo = Encoding.UTF8.GetBytes("ShinkansenVault/KEK/v1");

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length),
            output.AsSpan(NonceSize + plaintext.Length, TagSize), aad);
        return output;
    }

    /// <summary>Decrypts and authenticates. Throws <see cref="CryptographicException"/> on tampering or wrong key.</summary>
    public static byte[] Open(byte[] key, ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> aad)
    {
        if (sealedData.Length < NonceSize + TagSize) throw new CryptographicException("Sealed data too short.");
        int len = sealedData.Length - NonceSize - TagSize;
        var plaintext = new byte[len];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(sealedData[..NonceSize], sealedData.Slice(NonceSize, len),
            sealedData.Slice(NonceSize + len, TagSize), plaintext, aad);
        return plaintext;
    }

    /// <summary>KEK = HKDF-SHA256(ikm = Hello signature over the vault challenge, salt = per-vault salt).</summary>
    public static byte[] DeriveKek(byte[] helloSignature, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, helloSignature, KeySize, salt, KekInfo);

    public static void Zero(byte[]? data)
    {
        if (data != null) CryptographicOperations.ZeroMemory(data);
    }
}
