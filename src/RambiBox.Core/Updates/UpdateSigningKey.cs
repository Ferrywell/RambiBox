namespace RambiBox.Core.Updates;

/// <summary>
/// Public half of the release signing key (ECDSA P-256). The release workflow signs RambiBox.exe with the private
/// key (GitHub Actions secret RELEASE_SIGNING_KEY; an offline backup is kept by the maintainer) and uploads the DER
/// signature as RambiBox.exe.sig. The updater installs nothing whose signature doesn't verify against this key.
/// Changing it means older versions can no longer update themselves — see SECURITY.md before rotating.
/// </summary>
public static class UpdateSigningKey
{
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjVMHJXbJZmfSKxxc735sDaXxEHBX
        Qk7IKtPkaUuKHIWeupC+wAZ9ZNV4Ay1NAKfEJH4mHJNeGjL12E4O298c5Q==
        -----END PUBLIC KEY-----
        """;
}
