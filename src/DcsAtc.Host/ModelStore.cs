using System.Diagnostics;
using DcsAtc.Recognition;
using DcsAtc.Voice;

namespace DcsAtc.Host;

public enum ModelKind
{
    /// <summary>A controller voice (Kokoro, Piper).</summary>
    Voice,
    /// <summary>Speech recognition for voice requests (Whisper, Parakeet).</summary>
    Recognition,
}

/// <summary>A model the voices or the recognizers use: what for, its license, whether it is downloaded.</summary>
/// <param name="Url">Null: made with tools\Convert-WhisperModel.ps1, not downloaded.</param>
/// <param name="Default">The default voice or recognizer uses it.</param>
public sealed record ModelEntry(ModelKind Kind, string Name, string Use, string License, int? SizeMb, string? Url, bool IsArchive, string Path,
    bool Present, bool Default);

/// <summary>
/// The models in data\voice\voices.json and recognition.json, and their downloads: an archive (.tar.bz2) unpacks to
/// models\&lt;name&gt;\, a single file becomes models\&lt;name&gt;.bin. Everything is downloaded into a folder of its
/// own and moved into place only when complete.
/// </summary>
public sealed class ModelStore(VoiceCatalog? voices, RecognizerCatalog? recognizers)
{
    public VoiceCatalog? Voices { get; } = voices;
    public RecognizerCatalog? Recognizers { get; } = recognizers;

    /// <summary>The catalogs from the settings' data folder, with the models where the settings keep them.</summary>
    public static ModelStore Load(DigiTowerSettings settings)
    {
        string? voice = settings.ResolvedDataDirectory is { } data ? Path.Combine(data, "voice") : null;
        return new ModelStore(
            voice is not null && File.Exists(Path.Combine(voice, "voices.json")) ? VoiceCatalog.Load(voice, settings.ResolvedModelsDirectory, settings.ResolvedVoiceCacheDirectory) : null,
            voice is not null && File.Exists(Path.Combine(voice, RecognizerCatalog.File)) ? RecognizerCatalog.Load(voice, settings.ResolvedModelsDirectory, RecognizerCatalog.UserFile) : null);
    }

    public IReadOnlyList<ModelEntry> Entries()
    {
        var list = new List<ModelEntry>();
        if (Voices is { } v)
        {
            var defaultModel = v.Find(v.Default)?.Model;
            foreach (var m in v.Models)
            {
                var users = v.Voices.Where(x => x.Model == m.Name).ToList();
                string use = users.Count == 1 ? $"Voice: {users[0].Label}" : $"{users.Count} voices ({string.Join(", ", users.Select(u => u.Label.Split(':').Last().Trim().Split(' ')[0]))})";
                string path = Path.Combine(v.ModelsDirectory, m.Name);
                list.Add(new(ModelKind.Voice, m.Name, use, m.License, null, m.Url, true, path, Directory.Exists(path), m.Name == defaultModel));
            }
        }
        if (Recognizers is { } r)
        {
            var defaultModel = r.Find(r.Default)?.Model;
            foreach (var m in r.Models)
            {
                var users = r.Recognizers.Where(x => x.Model == m.Name).ToList();
                if (users.Count == 0) continue;
                string path = r.PathOf(m);
                list.Add(new(ModelKind.Recognition, m.Name, $"Voice requests: {users[0].Label.Split(',')[0].Split('(')[0].Trim()}", m.License, m.SizeMb, m.Url, m.IsArchive,
                    path, m.IsArchive ? Directory.Exists(path) : File.Exists(path), m.Name == defaultModel));
            }
        }
        return list;
    }

    /// <summary>Downloads a model; <paramref name="progress"/> gets 0…1 (or -1 while unpacking).</summary>
    public static async Task DownloadAsync(ModelEntry model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (model.Url is null) throw new InvalidOperationException($"{model.Name} is not downloaded but made with tools\\Convert-WhisperModel.ps1");
        if (Directory.Exists(model.Path) || File.Exists(model.Path)) return;
        string models = Path.GetDirectoryName(model.Path)!;
        string staging = Path.Combine(models, $"_download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string download = Path.Combine(staging, model.IsArchive ? model.Name + ".tar.bz2" : model.Name + ".bin");
            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            using (var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(download);
                var buffer = new byte[1 << 16];
                long done = 0;
                var reported = Stopwatch.StartNew();
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total is > 0 && reported.ElapsedMilliseconds > 200)
                    {
                        progress?.Report((double)done / total.Value);
                        reported.Restart();
                    }
                }
            }
            if (!model.IsArchive)
            {
                File.Move(download, model.Path);
            }
            else
            {
                progress?.Report(-1);
                using var tar = Process.Start(new ProcessStartInfo("tar", ["-xjf", download, "-C", staging]) { UseShellExecute = false, CreateNoWindow = true })!;
                await tar.WaitForExitAsync(ct);
                if (tar.ExitCode != 0 || !Directory.Exists(Path.Combine(staging, model.Name)))
                    throw new InvalidDataException($"{model.Name}: the archive did not unpack to {model.Name}\\");
                Directory.Move(Path.Combine(staging, model.Name), model.Path);
            }
            progress?.Report(1);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // tar may still hold a file after a cancel: the next download uses a new folder.
            }
        }
    }

    /// <summary>Deletes a downloaded model.</summary>
    public static void Remove(ModelEntry model)
    {
        if (Directory.Exists(model.Path)) Directory.Delete(model.Path, recursive: true);
        else if (File.Exists(model.Path)) File.Delete(model.Path);
    }

    /// <summary>The size of a downloaded model on disk (MB).</summary>
    public static int? SizeOnDisk(ModelEntry model)
    {
        long bytes = Directory.Exists(model.Path) ? new DirectoryInfo(model.Path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : File.Exists(model.Path) ? new FileInfo(model.Path).Length : 0;
        return bytes > 0 ? (int)(bytes / 1_000_000) : null;
    }
}
