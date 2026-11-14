using Lineup.Core.VirtualOutputs.Protocol;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>Resolves trusted-host certificate references into activation-scoped worker material.</summary>
public interface IVirtualOutputCertificateResolver
{
    /// <summary>Resolves one configured certificate reference.</summary>
    Task<VirtualOutputCertificateMaterial> ResolveAsync(string certificateReference, CancellationToken cancellationToken = default);
}

/// <summary>Resolves passwordless PKCS#12 files without exposing their host path to output workers.</summary>
public sealed class FileVirtualOutputCertificateResolver : IVirtualOutputCertificateResolver
{
    private const int MaximumCertificateBytes = 512 * 1024;

    /// <summary>Resolves the file virtual output certificate resolver asynchronously.</summary>
    /// <param name="certificateReference">The certificate reference value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async Task<VirtualOutputCertificateMaterial> ResolveAsync(string certificateReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateReference);
        var path = Path.GetFullPath(certificateReference);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The virtual-output TLS certificate could not be resolved.", path);
        }
        if (info.Length is < 1 or > MaximumCertificateBytes)
        {
            throw new InvalidDataException($"The virtual-output TLS certificate must be between 1 and {MaximumCertificateBytes} bytes.");
        }
        return new VirtualOutputCertificateMaterial
        {
            Reference = certificateReference,
            Pkcs12 = await File.ReadAllBytesAsync(path, cancellationToken)
        };
    }
}
