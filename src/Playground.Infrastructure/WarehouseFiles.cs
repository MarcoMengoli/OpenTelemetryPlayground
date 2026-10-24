using System.Security.Cryptography;
using System.Text;

namespace Playground.Infrastructure;

public enum FilePublicationStage { Flushed, Renamed }
public sealed class WarehouseFileConflictException(string path) : Exception($"Different content already exists at {path}.");

// Only warehouse filesystem mechanics; business identity and durable state stay in each service.
public static class WarehouseFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Name(string kind, string reference, bool duplicate = false)
    {
        if (kind is not ("request" or "response")) throw new ArgumentException("Unknown warehouse file kind.");
        var safeId = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(reference))).ToLowerInvariant();
        return kind + "-" + safeId + (duplicate ? "-duplicate" : "") + ".json";
    }
    public static string Read(string path) => Utf8.GetString(File.ReadAllBytes(path));
    public static void Publish(string directory, string name, string json, Action<FilePublicationStage>? checkpoint = null)
    {
        if (name != Path.GetFileName(name)) throw new ArgumentException("File name must not contain a path.");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, name);
        if (File.Exists(target)) { Verify(target, json); return; }
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(Utf8.GetBytes(json));
            file.Flush(flushToDisk: true);
        }
        // An interrupted publisher deliberately leaves this temp file for its next startup.
        checkpoint?.Invoke(FilePublicationStage.Flushed);
        try { File.Move(temporary, target, overwrite: false); }
        catch (IOException) when (File.Exists(target))
        {
            Verify(target, json);
            File.Delete(temporary);
            return;
        }
        checkpoint?.Invoke(FilePublicationStage.Renamed);
    }
    private static void Verify(string target, string json)
    {
        try
        {
            if (Read(target) != json) throw new WarehouseFileConflictException(target);
        }
        catch (DecoderFallbackException) { throw new WarehouseFileConflictException(target); }
    }
    public static string Archive(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        // Never trust an input filename for the destination and never overwrite old evidence.
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.Move(source, destination, overwrite: false);
        return destination;
    }
    // Call only once on startup, with the service's exclusive writer lease held.
    public static void RemoveOrphanTemps(string directory, string kind)
    {
        if (kind is not ("request" or "response")) throw new ArgumentException("Unknown warehouse file kind.");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, kind + "-*.json.tmp-*")) File.Delete(file);
    }
    public static FileStream AcquireWriterLease(string directory, string kind)
    {
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "." + kind + "-writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
}