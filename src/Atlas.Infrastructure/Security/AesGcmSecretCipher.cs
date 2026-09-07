using System.Security.Cryptography;
using Atlas.Application.Credentials;

namespace Atlas.Infrastructure.Security;

public sealed class SecretCipherOptions
{
    public const string SectionName = "Atlas:Secrets";

    /// <summary>32 random bytes, base64. Losing it makes every stored credential unrecoverable (rotate them instead).</summary>
    public string? MasterKeyBase64 { get; set; }
}

/// <summary>
/// AES-256-GCM envelope: [version=1][nonce 12][tag 16][ciphertext]. A fresh
/// random nonce per Protect call; tampering fails authentication and throws.
/// </summary>
public sealed class AesGcmSecretCipher : ISecretCipher
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _key;

    public AesGcmSecretCipher(SecretCipherOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MasterKeyBase64))
        {
            return;
        }

        var key = Convert.FromBase64String(options.MasterKeyBase64);
        if (key.Length != 32)
        {
            throw new InvalidOperationException("Atlas:Secrets:MasterKeyBase64 must decode to exactly 32 bytes (AES-256).");
        }

        _key = key;
    }

    public bool IsConfigured => _key is not null;

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var key = _key ?? throw new SecretStoreNotConfiguredException();
        var envelope = new byte[1 + NonceSize + TagSize + plaintext.Length];
        envelope[0] = Version;

        var nonce = envelope.AsSpan(1, NonceSize);
        var tag = envelope.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = envelope.AsSpan(1 + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return envelope;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> envelope)
    {
        var key = _key ?? throw new SecretStoreNotConfiguredException();
        if (envelope.Length < 1 + NonceSize + TagSize || envelope[0] != Version)
        {
            throw new CryptographicException("Unrecognized secret envelope.");
        }

        var nonce = envelope.Slice(1, NonceSize);
        var tag = envelope.Slice(1 + NonceSize, TagSize);
        var ciphertext = envelope[(1 + NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
