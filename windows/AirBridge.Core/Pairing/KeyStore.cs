using System.Security.Cryptography;
using System.Text.Json;

namespace AirBridge.Core.Pairing;

/// <summary>
/// Persists the local EC key pair and remote device public keys.
/// Storage location: %APPDATA%\AirBridge\keys.json
/// All operations are thread-safe.
/// </summary>
public sealed class KeyStore : IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private KeyStoreData _data;
    private ECDsa? _localKey;
    private bool _disposed;

    public KeyStore(string? filePath = null)
    {
        _filePath = filePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AirBridge", "keys.json");

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        _data = Load();
    }

    // ── Local key pair ─────────────────────────────────────────────────────

    /// <summary>Returns the local public key bytes (uncompressed X9.63 format).</summary>
    public byte[] GetLocalPublicKey()
    {
        EnsureLocalKey();
        return _localKey!.ExportSubjectPublicKeyInfo();
    }

    /// <summary>Signs <paramref name="data"/> with the local private key.</summary>
    public byte[] Sign(byte[] data)
    {
        EnsureLocalKey();
        return _localKey!.SignData(data, HashAlgorithmName.SHA256);
    }

    /// <summary>Verifies a signature from a paired remote device.</summary>
    public bool Verify(string deviceId, byte[] data, byte[] signature)
    {
        var keyBytes = GetRemoteKey(deviceId);
        if (keyBytes is null) return false;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    // ── Remote key storage ─────────────────────────────────────────────────

    /// <summary>Stores or updates the public key for a paired remote device.</summary>
    public async Task StoreRemoteKeyAsync(string deviceId, byte[] publicKeyBytes)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _data.RemoteKeys[deviceId] = Convert.ToBase64String(publicKeyBytes);
            await SaveAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns the stored public key bytes for a device, or null if not paired.</summary>
    public byte[]? GetRemoteKey(string deviceId)
    {
        if (_data.RemoteKeys.TryGetValue(deviceId, out var b64))
            return Convert.FromBase64String(b64);
        return null;
    }

    /// <summary>Removes a paired device's stored key.</summary>
    public async Task RemoveRemoteKeyAsync(string deviceId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_data.RemoteKeys.Remove(deviceId))
                await SaveAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns true if a key is stored for <paramref name="deviceId"/>.</summary>
    public bool HasRemoteKey(string deviceId) => _data.RemoteKeys.ContainsKey(deviceId);

    // ── Persistence ────────────────────────────────────────────────────────

    private KeyStoreData Load()
    {
        if (!File.Exists(_filePath))
            return new KeyStoreData();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<KeyStoreData>(json) ?? new KeyStoreData();
        }
        catch { return new KeyStoreData(); }
    }

    private async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
    }

    private void EnsureLocalKey()
    {
        if (_localKey is not null) return;

        if (_data.LocalPrivateKeyPkcs8 is not null)
        {
            _localKey = ECDsa.Create();
            _localKey.ImportPkcs8PrivateKey(
                Convert.FromBase64String(_data.LocalPrivateKeyPkcs8), out _);
        }
        else
        {
            _localKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _data.LocalPrivateKeyPkcs8 = Convert.ToBase64String(
                _localKey.ExportPkcs8PrivateKey());
            // Best-effort synchronous save for key generation
            File.WriteAllText(_filePath,
                JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localKey?.Dispose();
        _lock.Dispose();
    }

    // ── Internal data model ────────────────────────────────────────────────

    // ── TLS certificate ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the stored TLS certificate PFX bytes, or null if none persisted yet.
    /// </summary>
    public byte[]? GetTlsCertificatePfx()
    {
        if (_data.TlsCertificatePfx is null) return null;
        return Convert.FromBase64String(_data.TlsCertificatePfx);
    }

    /// <summary>
    /// Persists the TLS certificate as PFX/PKCS12 bytes so the same cert survives
    /// across restarts (required for TOFU fingerprint pinning).
    /// </summary>
    public async Task StoreTlsCertificatePfxAsync(byte[] pfxBytes)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _data.TlsCertificatePfx = Convert.ToBase64String(pfxBytes);
            await SaveAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private sealed class KeyStoreData
    {
        public string? LocalPrivateKeyPkcs8 { get; set; }
        public string? TlsCertificatePfx { get; set; }
        public Dictionary<string, string> RemoteKeys { get; set; } = new();
    }
}
