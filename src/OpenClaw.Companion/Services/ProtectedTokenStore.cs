using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace OpenClaw.Companion.Services;

public interface ICompanionSecretStore
{
    string StorageDescription { get; }

    bool IsAvailable { get; }

    string? LoadSecret(out string? warning);

    bool SaveSecret(string secret, out string? warning);

    void ClearSecret();
}

public sealed class ProtectedTokenStore
{
    private readonly ICompanionSecretStore _secureStore;
    private readonly string _fallbackPath;

    public string? LastWarning { get; private set; }

    public string ProtectedPath => _secureStore.StorageDescription;

    public string FallbackPath => _fallbackPath;

    public ProtectedTokenStore(string? baseDir = null, ICompanionSecretStore? secureStore = null)
    {
        var resolvedBaseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClaw",
            "Companion");
        Directory.CreateDirectory(resolvedBaseDir);

        _fallbackPath = Path.Combine(resolvedBaseDir, "token.txt");
        _secureStore = secureStore ?? CompanionSecretStoreFactory.CreateDefault(resolvedBaseDir);
    }

    public string? LoadToken(bool allowPlaintextFallback)
    {
        LastWarning = null;

        if (_secureStore.IsAvailable)
        {
            var token = _secureStore.LoadSecret(out var warning);
            LastWarning = warning;
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }
        else
        {
            LastWarning = "Secure token storage is unavailable on this system.";
        }

        if (!File.Exists(_fallbackPath))
            return null;

        if (!allowPlaintextFallback)
        {
            LastWarning = LastWarning is null
                ? "A plaintext companion token exists, but plaintext fallback is disabled."
                : $"{LastWarning} Plaintext fallback is disabled.";
            return null;
        }

        LastWarning = LastWarning is null
            ? "Using plaintext companion token fallback storage."
            : $"{LastWarning} Plaintext fallback was used.";
        return File.ReadAllText(_fallbackPath);
    }

    public bool SaveToken(string token, bool allowPlaintextFallback, out string? warning)
    {
        warning = null;
        Directory.CreateDirectory(Path.GetDirectoryName(_fallbackPath)!);

        if (_secureStore.IsAvailable && _secureStore.SaveSecret(token, out warning))
        {
            TryDelete(_fallbackPath);
            LastWarning = warning;
            return true;
        }

        warning ??= _secureStore.IsAvailable
            ? "Secure token storage failed."
            : "Secure token storage is unavailable on this system.";

        if (!allowPlaintextFallback)
        {
            TryDelete(_fallbackPath);
            warning = $"{warning} Token was not saved because plaintext fallback is disabled.";
            LastWarning = warning;
            return false;
        }

        File.WriteAllText(_fallbackPath, token);
        warning = $"{warning} Plaintext fallback was used.";
        LastWarning = warning;
        return false;
    }

    public void ClearToken()
    {
        _secureStore.ClearSecret();
        TryDelete(_fallbackPath);
        LastWarning = null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

internal static class CompanionSecretStoreFactory
{
    public static ICompanionSecretStore CreateDefault(string baseDir)
    {
        if (OperatingSystem.IsMacOS())
            return new MacOsKeychainSecretStore(baseDir);

        if (OperatingSystem.IsWindows())
            return new WindowsDpapiSecretStore(baseDir);

        if (OperatingSystem.IsLinux() && ProcessCommandSecretStore.IsCommandAvailable("secret-tool"))
            return new LinuxSecretToolSecretStore(baseDir);

        return new UnavailableSecretStore("unavailable");
    }
}

internal static class CompanionSecretStoreNaming
{
    public static string BuildScopedAccountName(string baseDir)
        => "auth-token-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseDir))).Substring(0, 16);
}

[SupportedOSPlatform("macos")]
internal sealed class MacOsKeychainSecretStore : ICompanionSecretStore
{
    private readonly string _serviceName = "OpenClaw.Companion";
    private readonly string _accountName;

    public MacOsKeychainSecretStore(string baseDir)
    {
        _accountName = CompanionSecretStoreNaming.BuildScopedAccountName(baseDir);
    }

    public string StorageDescription => $"keychain:{_serviceName}/{_accountName}";

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public string? LoadSecret(out string? warning)
    {
        var status = MacOsKeychainNative.LoadSecret(_serviceName, _accountName, out var secret);
        warning = status switch
        {
            MacOsKeychainNative.ErrSecSuccess => null,
            MacOsKeychainNative.ErrSecItemNotFound => null,
            _ => $"Failed to load token from macOS Keychain. OSStatus={status}."
        };
        return status == MacOsKeychainNative.ErrSecSuccess ? secret : null;
    }

    public bool SaveSecret(string secret, out string? warning)
    {
        var status = MacOsKeychainNative.SaveSecret(_serviceName, _accountName, secret);
        warning = status == MacOsKeychainNative.ErrSecSuccess
            ? null
            : $"Failed to save token in macOS Keychain. OSStatus={status}.";
        return status == MacOsKeychainNative.ErrSecSuccess;
    }

    public void ClearSecret()
    {
        _ = MacOsKeychainNative.DeleteSecret(_serviceName, _accountName);
    }
}

[SupportedOSPlatform("macos")]
internal static class MacOsKeychainNative
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const int ErrSecSuccess = 0;
    public const int ErrSecDuplicateItem = -25299;
    public const int ErrSecItemNotFound = -25300;

    public static int LoadSecret(string serviceName, string accountName, out string? secret)
    {
        secret = null;
        var serviceBytes = Encoding.UTF8.GetBytes(serviceName);
        var accountBytes = Encoding.UTF8.GetBytes(accountName);
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            out var passwordLength,
            out var passwordData,
            out var itemRef);

        try
        {
            if (status != ErrSecSuccess)
                return status;

            if (passwordLength == 0 || passwordData == IntPtr.Zero)
            {
                secret = string.Empty;
                return ErrSecSuccess;
            }

            var bytes = new byte[passwordLength];
            Marshal.Copy(passwordData, bytes, 0, bytes.Length);
            secret = Encoding.UTF8.GetString(bytes);
            return ErrSecSuccess;
        }
        finally
        {
            if (passwordData != IntPtr.Zero)
                _ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            Release(itemRef);
        }
    }

    public static int SaveSecret(string serviceName, string accountName, string secret)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(serviceName);
        var accountBytes = Encoding.UTF8.GetBytes(accountName);
        var secretBytes = Encoding.UTF8.GetBytes(secret);

        var status = SecKeychainAddGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            (uint)secretBytes.Length,
            secretBytes,
            out var addedItemRef);
        try
        {
            if (status == ErrSecSuccess)
                return status;
            if (status != ErrSecDuplicateItem)
                return status;
        }
        finally
        {
            Release(addedItemRef);
        }

        status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            out _,
            out var existingPasswordData,
            out var itemRef);
        try
        {
            if (status != ErrSecSuccess)
                return status;

            return SecKeychainItemModifyAttributesAndData(itemRef, IntPtr.Zero, (uint)secretBytes.Length, secretBytes);
        }
        finally
        {
            if (existingPasswordData != IntPtr.Zero)
                _ = SecKeychainItemFreeContent(IntPtr.Zero, existingPasswordData);
            Release(itemRef);
        }
    }

    public static int DeleteSecret(string serviceName, string accountName)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(serviceName);
        var accountBytes = Encoding.UTF8.GetBytes(accountName);
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            out _,
            out var passwordData,
            out var itemRef);
        try
        {
            if (status == ErrSecItemNotFound)
                return ErrSecSuccess;
            if (status != ErrSecSuccess)
                return status;

            return SecKeychainItemDelete(itemRef);
        }
        finally
        {
            if (passwordData != IntPtr.Zero)
                _ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            Release(itemRef);
        }
    }

    private static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            CFRelease(handle);
    }

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef,
        IntPtr attrList,
        uint length,
        byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr cf);
}

 [SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiSecretStore : ICompanionSecretStore
{
    private readonly string _ciphertextPath;

    public WindowsDpapiSecretStore(string baseDir)
    {
        _ciphertextPath = Path.Combine(baseDir, "token.dpapi");
    }

    public string StorageDescription => _ciphertextPath;

    public bool IsAvailable => true;

    public string? LoadSecret(out string? warning)
    {
        warning = null;
        if (!File.Exists(_ciphertextPath))
            return null;

        try
        {
            if (!OperatingSystem.IsWindows())
                return null;

            var protectedBytes = File.ReadAllBytes(_ciphertextPath);
            var secretBytes = Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(secretBytes);
        }
        catch (Exception ex)
        {
            warning = $"Failed to unlock Windows protected token storage. {ex.Message}";
            return null;
        }
    }

    public bool SaveSecret(string secret, out string? warning)
    {
        warning = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                warning = "Windows protected storage is unavailable on this system.";
                return false;
            }

            var protectedBytes = Protect(Encoding.UTF8.GetBytes(secret));
            File.WriteAllBytes(_ciphertextPath, protectedBytes);
            return true;
        }
        catch (Exception ex)
        {
            warning = $"Failed to save token in Windows protected storage. {ex.Message}";
            return false;
        }
    }

    public void ClearSecret()
    {
        try { File.Delete(_ciphertextPath); } catch { }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] secret)
        => ProtectedData.Protect(secret, optionalEntropy: null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] secret)
        => ProtectedData.Unprotect(secret, optionalEntropy: null, DataProtectionScope.CurrentUser);
}

internal sealed class LinuxSecretToolSecretStore : ICompanionSecretStore
{
    private readonly string _accountName;

    public LinuxSecretToolSecretStore(string baseDir)
    {
        _accountName = CompanionSecretStoreNaming.BuildScopedAccountName(baseDir);
    }

    public string StorageDescription => $"secret-service:openclaw-companion/{_accountName}";

    public bool IsAvailable => ProcessCommandSecretStore.IsCommandAvailable("secret-tool");

    public string? LoadSecret(out string? warning)
    {
        warning = null;
        var result = ProcessCommandSecretStore.Run(
            "secret-tool",
            ["lookup", "service", "openclaw-companion", "account", _accountName]);
        if (result.ExitCode == 0)
            return result.StdOut.TrimEnd();

        if (!string.IsNullOrWhiteSpace(result.StdErr))
            warning = $"Failed to load token from Linux Secret Service. {result.StdErr.Trim()}";
        return null;
    }

    public bool SaveSecret(string secret, out string? warning)
    {
        var result = ProcessCommandSecretStore.Run(
            "secret-tool",
            ["store", "--label=OpenClaw Companion Auth Token", "service", "openclaw-companion", "account", _accountName],
            stdin: secret);
        warning = result.ExitCode == 0
            ? null
            : $"Failed to save token in Linux Secret Service. {result.StdErr.Trim()}";
        return result.ExitCode == 0;
    }

    public void ClearSecret()
    {
        _ = ProcessCommandSecretStore.Run(
            "secret-tool",
            ["clear", "service", "openclaw-companion", "account", _accountName]);
    }
}

internal sealed class UnavailableSecretStore : ICompanionSecretStore
{
    public UnavailableSecretStore(string storageDescription)
    {
        StorageDescription = storageDescription;
    }

    public string StorageDescription { get; }

    public bool IsAvailable => false;

    public string? LoadSecret(out string? warning)
    {
        warning = "Secure token storage is unavailable on this system.";
        return null;
    }

    public bool SaveSecret(string secret, out string? warning)
    {
        warning = "Secure token storage is unavailable on this system.";
        return false;
    }

    public void ClearSecret()
    {
    }
}

internal static class ProcessCommandSecretStore
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static bool IsCommandAvailable(string command)
    {
        try
        {
            var result = Run("/usr/bin/env", ["which", command], timeout: TimeSpan.FromSeconds(3));
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
        }
        catch
        {
            return false;
        }
    }

    public static (int ExitCode, string StdOut, string StdErr) Run(string fileName, IReadOnlyList<string> arguments, string? stdin = null, TimeSpan? timeout = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        var effectiveTimeout = timeout ?? DefaultTimeout;
        using var cts = new CancellationTokenSource(effectiveTimeout);
        try
        {
            process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(2));
            var timedOutStdOut = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            var timedOutStdErr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            var timeoutMessage = $"Secure store command '{fileName}' timed out after {effectiveTimeout.TotalSeconds:0.#} seconds.";
            timedOutStdErr = string.IsNullOrWhiteSpace(timedOutStdErr)
                ? timeoutMessage
                : $"{timedOutStdErr.TrimEnd()}{Environment.NewLine}{timeoutMessage}";
            return (-1, timedOutStdOut, timedOutStdErr);
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
