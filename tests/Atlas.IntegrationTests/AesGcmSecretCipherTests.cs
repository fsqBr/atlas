using System.Security.Cryptography;
using System.Text;
using Atlas.Application.Credentials;
using Atlas.Infrastructure.Security;

namespace Atlas.IntegrationTests;

public class AesGcmSecretCipherTests
{
    private static AesGcmSecretCipher Configured() =>
        new(new SecretCipherOptions { MasterKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });

    [Fact]
    public void Round_trips_and_uses_a_fresh_nonce_each_time()
    {
        var cipher = Configured();
        var plain = Encoding.UTF8.GetBytes("ghp_secret-token-value");

        var a = cipher.Protect(plain);
        var b = cipher.Protect(plain);

        Assert.NotEqual(a, b);
        Assert.Equal(plain, cipher.Unprotect(a));
        Assert.Equal(plain, cipher.Unprotect(b));
    }

    [Fact]
    public void Tampering_is_detected()
    {
        var cipher = Configured();
        var envelope = cipher.Protect(Encoding.UTF8.GetBytes("token"));
        envelope[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Unprotect(envelope));
    }

    [Fact]
    public void Different_master_keys_cannot_read_each_other()
    {
        var envelope = Configured().Protect(Encoding.UTF8.GetBytes("token"));
        Assert.ThrowsAny<CryptographicException>(() => Configured().Unprotect(envelope));
    }

    [Fact]
    public void Unconfigured_cipher_reports_it_and_refuses_to_work()
    {
        var cipher = new AesGcmSecretCipher(new SecretCipherOptions());
        Assert.False(cipher.IsConfigured);
        Assert.Throws<SecretStoreNotConfiguredException>(() => cipher.Protect("x"u8));
    }

    [Fact]
    public void Rejects_keys_that_are_not_32_bytes()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AesGcmSecretCipher(new SecretCipherOptions { MasterKeyBase64 = Convert.ToBase64String(new byte[16]) }));
    }
}
