using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Infrastructure.Storage;

public sealed class FileProfileStore : IProfileFileStore
{
    private const int Attempts = 3;

    public FileAccessState Inspect(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return FileAccessState.Missing;
            if (info.IsReadOnly) return FileAccessState.ReadOnly;
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return FileAccessState.Ok;
        }
        catch (UnauthorizedAccessException) { return FileAccessState.Denied; }
        catch (IOException) { return FileAccessState.Locked; }
    }

    public async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var buffer = new byte[fs.Length];
        await fs.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    public async Task WriteAtomicAsync(string path, byte[] content, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.IsReadOnly) throw new OptimizerException(new Msg("error.file_readonly", path));

        var dir = info.DirectoryName!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{info.Name}.{Guid.NewGuid():N}.bf6tmp");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await fs.WriteAsync(content, ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Swap(tmp, path);
                    return;
                }
                catch (IOException) when (attempt < Attempts)
                {
                    // Sync clients and scanners hold files briefly.
                    await Task.Delay(100 * attempt, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException) { }
        }
    }

    private static void Swap(string tmp, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }
        try
        {
            File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            // Some network and FAT volumes lack ReplaceFile. Verification still covers this path.
            File.Copy(tmp, path, overwrite: true);
        }
    }
}
