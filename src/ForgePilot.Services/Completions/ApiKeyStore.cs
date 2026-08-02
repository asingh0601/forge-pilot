using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgePilot.Services.Completions;

/// <summary>
/// Stores the Anthropic API key used for inline completions, encrypted with
/// DPAPI under the current user.
///
/// It deliberately does not live in the Visual Studio settings store. A
/// <c>DialogPage</c> property is persisted to the VS registry hive as
/// plaintext, readable by anything running as that user and easily captured in
/// a settings export or a screen share of the options grid. DPAPI at least
/// binds the ciphertext to the user account.
///
/// This is not a secrets vault: an attacker already running code as this user
/// can call Unprotect just as easily. It defends against casual disclosure -
/// exports, backups, shoulder-surfing the property grid - not against local
/// compromise.
/// </summary>
public sealed class ApiKeyStore
{
    private const string FileName = "credentials.dat";

    // Ties the ciphertext to this purpose, so a blob lifted from here cannot be
    // fed to an unrelated Unprotect call that expects different data.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ForgePilot.Completions.v1");

    private readonly string _path;
    private readonly ILogger _logger;

    public ApiKeyStore(ILogger<ApiKeyStore>? logger = null)
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ForgePilot",
            FileName);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public bool HasKey => File.Exists(_path);

    /// <summary>
    /// Returns the decrypted key, or null when none is stored or the blob
    /// cannot be decrypted — which happens legitimately when the file is copied
    /// to another machine or user profile.
    /// </summary>
    public string? Read()
    {
        if (!File.Exists(_path)) return null;

        try
        {
            var cipher = File.ReadAllBytes(_path);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Completions] Stored API key could not be decrypted; treating as unset");
            return null;
        }
    }

    /// <summary>
    /// Encrypts and stores the key. Passing null or whitespace deletes it,
    /// which is how the user clears the setting.
    /// </summary>
    public void Write(string? apiKey)
    {
        try
        {
            if (apiKey is null || string.IsNullOrWhiteSpace(apiKey))
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(apiKey.Trim()), Entropy, DataProtectionScope.CurrentUser);

            File.WriteAllBytes(_path, cipher);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Completions] Failed to store API key");
        }
    }

    /// <summary>
    /// What the options grid shows in place of the key: enough to confirm which
    /// key is set, not enough to reconstruct it.
    /// </summary>
    public string MaskedDisplay()
    {
        var key = Read();
        if (key is null || key.Length == 0) return "";
        return key.Length <= 8 ? "••••" : "••••••••" + key.Substring(key.Length - 4);
    }
}
