using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Security;

public sealed class WindowsDataProtection : IWindowsKeyProtection
{
    private const int CryptProtectUiForbidden = 0x1;

    public ValueTask<ControllerResult<ProtectedKeyBlob>> ProtectAsync(
        ProtectKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsStructurallyValid)
        {
            return ValueTask.FromResult(ControllerResult<ProtectedKeyBlob>.Failure(InvalidRequest()));
        }

        try
        {
            var protectedBytes = Protect(
                request.Plaintext.Span,
                Entropy(request.Context, request.Purpose));
            return ValueTask.FromResult(ProtectedKeyBlob.Create("dpapi-user-v1", protectedBytes));
        }
        catch (Exception exception) when (exception is Win32Exception or CryptographicException)
        {
            return ValueTask.FromResult(ControllerResult<ProtectedKeyBlob>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.Unavailable,
                    "error.key_protection.protect_failed")));
        }
    }

    public ValueTask<ControllerResult<IUnprotectedKeyMaterial>> UnprotectAsync(
        UnprotectKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsStructurallyValid || request.ProtectedBlob.Format != "dpapi-user-v1")
        {
            return ValueTask.FromResult(ControllerResult<IUnprotectedKeyMaterial>.Failure(InvalidRequest()));
        }

        try
        {
            var plaintext = Unprotect(
                request.ProtectedBlob.Bytes.Span,
                Entropy(request.Context, request.Purpose));
            return ValueTask.FromResult(ControllerResult<IUnprotectedKeyMaterial>.Success(
                new UnprotectedKeyMaterial(plaintext)));
        }
        catch (Exception exception) when (exception is Win32Exception or CryptographicException)
        {
            return ValueTask.FromResult(ControllerResult<IUnprotectedKeyMaterial>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "error.key_protection.unprotect_failed")));
        }
    }

    private static ControllerError InvalidRequest() =>
        ControllerError.Create(ControllerErrorCode.InvalidRequest, "error.key_protection.request_invalid");

    private static byte[] Entropy(PrivacyContext context, WindowsKeyProtectionPurpose purpose) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"orbit-navigator|dpapi-v1|{context.ProfileId.Value:N}|{purpose}"));

    private static byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
        Transform(plaintext, entropy, protect: true);

    private static byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
        Transform(ciphertext, entropy, protect: false);

    private static byte[] Transform(
        ReadOnlySpan<byte> inputBytes,
        ReadOnlySpan<byte> entropyBytes,
        bool protect)
    {
        using var input = NativeBlob.From(inputBytes);
        using var entropy = NativeBlob.From(entropyBytes);
        DataBlob output = default;
        var succeeded = protect
            ? CryptProtectData(
                ref input.Value,
                null,
                ref entropy.Value,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out output)
            : CryptUnprotectData(
                ref input.Value,
                IntPtr.Zero,
                ref entropy.Value,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out output);

        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private sealed class NativeBlob : IDisposable
    {
        private NativeBlob(DataBlob value) => Value = value;

        public DataBlob Value;

        public static NativeBlob From(ReadOnlySpan<byte> bytes)
        {
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            var temporary = bytes.ToArray();
            try
            {
                Marshal.Copy(temporary, 0, pointer, temporary.Length);
                return new NativeBlob(new DataBlob { Size = bytes.Length, Data = pointer });
            }
            catch
            {
                Marshal.FreeHGlobal(pointer);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(temporary);
            }
        }

        public void Dispose()
        {
            if (Value.Data == IntPtr.Zero)
            {
                return;
            }

            Span<byte> zeros = Value.Size <= 1024
                ? stackalloc byte[Value.Size]
                : new byte[Value.Size];
            Marshal.Copy(zeros.ToArray(), 0, Value.Data, Value.Size);
            Marshal.FreeHGlobal(Value.Data);
            Value = default;
        }
    }

    private sealed class UnprotectedKeyMaterial : IUnprotectedKeyMaterial
    {
        private byte[]? _bytes;

        public UnprotectedKeyMaterial(byte[] bytes) => _bytes = bytes;

        public ReadOnlyMemory<byte> Bytes => _bytes ?? ReadOnlyMemory<byte>.Empty;

        public void Dispose()
        {
            if (_bytes is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
