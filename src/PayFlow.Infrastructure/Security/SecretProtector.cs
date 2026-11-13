using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Infrastructure.Security;

public sealed class MasterKeyOptions
{
    public const string Section = "Security";

    /// <summary>Base64 AES-256 key (exactly 32 bytes). Supplied via secrets only: <c>openssl rand -base64 32</c>.</summary>
    [Sensitive]
    public string? MasterKey { get; set; }
}

/// <summary>
/// Encrypts secrets that PayFlow must be able to read back, such as webhook signing secrets and the
/// Data Protection key ring, with AES-256-GCM under a master key held outside the database.
/// <para>
/// Format: <c>v1.{base64(nonce ‖ tag ‖ ciphertext)}</c>. The purpose string is authenticated as
/// associated data, so a ciphertext produced for one purpose cannot be replayed for another. The
/// version prefix leaves room for key rotation (decrypt with the old key, re-encrypt with the new).
/// A database dump alone therefore reveals no usable secret.
/// </para>
/// </summary>
public sealed class SecretProtector
{
    private const string Version = "v1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SecretProtector(IOptions<MasterKeyOptions> options, IHostEnvironment environment, ILogger<SecretProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var configured = options.Value.MasterKey;
        if (!string.IsNullOrEmpty(configured))
        {
            _key = Convert.FromBase64String(configured);
            if (_key.Length != 32)
            {
                throw new InvalidOperationException("Security:MasterKey must decode to exactly 32 bytes (AES-256).");
            }
        }
        else if (environment.IsDevelopment())
        {
            logger.LogWarning("No Security:MasterKey configured; using an ephemeral key. Encrypted secrets will be unreadable after a restart.");
            _key = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            throw new InvalidOperationException("Security:MasterKey is required. Generate one with: openssl rand -base64 32");
        }
    }

    public string Protect(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + data.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(purpose));
        return Version + Convert.ToBase64String(output);
    }

    /// <summary>Throws <see cref="CryptographicException"/> if the value was tampered with or protected for another purpose or key.</summary>
    public string Unprotect(string protectedValue, string purpose)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        if (!protectedValue.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown protected-value format.");
        }

        var input = Convert.FromBase64String(protectedValue[Version.Length..]);
        if (input.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected value is truncated.");
        }

        var plaintext = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plaintext, Encoding.UTF8.GetBytes(purpose));
        return Encoding.UTF8.GetString(plaintext);
    }
}

/// <summary>Encrypts each Data Protection key (which signs session and antiforgery cookies) with the master key before it is stored.</summary>
internal sealed class MasterKeyXmlEncryptor(SecretProtector protector) : IXmlEncryptor
{
    public const string Purpose = "PayFlow.DataProtection.KeyRing";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var encrypted = new XElement(
            "encryptedKey",
            new XComment(" Encrypted with the PayFlow master key (AES-256-GCM). "),
            new XElement("value", protector.Protect(plaintextElement.ToString(SaveOptions.DisableFormatting), Purpose)));
        return new EncryptedXmlInfo(encrypted, typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>Created by Data Protection through its <see cref="IServiceProvider"/> constructor.</summary>
public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var value = encryptedElement.Element("value")?.Value ?? throw new CryptographicException("Encrypted key element has no value.");
        return XElement.Parse(services.GetRequiredService<SecretProtector>().Unprotect(value, MasterKeyXmlEncryptor.Purpose));
    }
}
