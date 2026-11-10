using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace MeetingRecorder.Model;

internal sealed class ModelStore(HttpClient http, string modelPath, string url, long size, string sha256)
{
    public bool IsPresent => File.Exists(modelPath) && new FileInfo(modelPath).Length == size;

    public async Task DownloadAsync(IProgress<(long done, long total)> progress, CancellationToken ct)
    {
        var part = modelPath + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(modelPath))!);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    hash.AppendData(buffer, 0, count);
                    done += count;
                    progress.Report((done, total));
                }
            }
            ct.ThrowIfCancellationRequested();
            if (done != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Model download is corrupt");
            File.Move(part, modelPath, overwrite: true);
        }
        catch
        {
            File.Delete(part);
            throw;
        }
    }
}
