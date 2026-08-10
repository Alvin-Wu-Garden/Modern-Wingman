using System.Security.Cryptography;
using System.Text;
using AgentService.Application.Contracts;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// 使用 Windows DPAPI CurrentUser 保護本機機密。
/// 密文只能由同一部 Windows 電腦上的同一位使用者解密，不提供跨平台降級儲存。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ModernWingman.LocalSecrets.v1");

    /// <summary>將明文轉成含版本化 scheme 的 Base64 DPAPI 密文。</summary>
    public ProtectedSecret Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Modern Wingman 僅支援 Windows 10／11 x64。");
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            Entropy,
            DataProtectionScope.CurrentUser);
        return new(Convert.ToBase64String(encrypted), "dpapi-current-user-v1");
    }

    /// <summary>驗證 scheme 後解密；未知或非 Windows 環境採 fail-closed。</summary>
    public string Unprotect(string value, string scheme) => scheme switch
    {
        "dpapi-current-user-v1" when OperatingSystem.IsWindows() => Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)),
        "dpapi-current-user-v1" => throw new PlatformNotSupportedException("Windows DPAPI credentials can only be read by the Windows user that saved them."),
        _ => throw new InvalidOperationException($"不支援的憑證加密方案：{scheme}"),
    };
}
