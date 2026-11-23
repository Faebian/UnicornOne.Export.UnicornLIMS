using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnicornOne.Abstractions.Export;

namespace UnicornOne.Export.UnicornLIMS
{
    /// <summary>
    /// Externalised version of Platform.Data.Mirror.DataSnapshotService.
    /// This is a complete instance-data mirror, NOT a SampleForm experiment export.
    /// Runs at startup and after the host's forced disk flush.
    /// </summary>
    public sealed class UnicornLimsExportPlugin : ISystemExportPlugin
    {
        public string Id => "unicornone.export.unicornlims";
        public string DisplayName => "UnicornLIMS incremental instance snapshot";
        public int ContractVersion => SystemExportContract.Version;

        public void Export(SystemExportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.InstanceDataPath) ||
                string.IsNullOrWhiteSpace(request.InstanceSnapshotPath))
                throw new ArgumentException("Instance source/destination paths are required.");

            string sourceRoot = Path.GetFullPath(request.InstanceDataPath);
            string snapshotRoot = Path.GetFullPath(request.InstanceSnapshotPath);
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
            // Prevent accidental recursive copying and self-mirroring.
            string sourcePrefix = sourceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string targetPrefix = snapshotRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (targetPrefix.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
                sourcePrefix.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sourceRoot, snapshotRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source and snapshot directories must not overlap.");

            CreateSnapshot(sourceRoot, snapshotRoot, request);
        }

        private static void CreateSnapshot(string sourceRoot, string targetRoot, SystemExportRequest request)
        {
            var timer = Stopwatch.StartNew();
            Directory.CreateDirectory(targetRoot);
            var previousByPath = ReadPreviousManifest(targetRoot);
            var result = new SnapshotResult
            {
                SnapshotUuid = Guid.NewGuid().ToString(),
                InstanceUuid = request.InstanceUuid,
                SystemUuid = request.SystemUuid,
                SystemName = request.SystemName,
                Fingerprint = request.Fingerprint,
                CreatedUtc = DateTime.UtcNow,
                SourceRoot = sourceRoot,
                SnapshotRoot = targetRoot
            };
            string building = Path.Combine(targetRoot, ".snapshot_building");
            string ready = Path.Combine(targetRoot, ".snapshot_ready");
            File.WriteAllText(building, DateTime.UtcNow.ToString("o"));
            if (File.Exists(ready)) File.Delete(ready);

            try
            {
                foreach (string file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    if (ShouldSkip(file)) continue;
                    string relative = RelativePath(sourceRoot, file);
                    string target = Path.Combine(targetRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    result.Files.Add(CopyIfChanged(file, target, relative, previousByPath));
                }

                // Keep orphaned records/files, matching existing UnicornLIMS behavior.
                foreach (string oldFile in Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories))
                {
                    if (ShouldSkip(oldFile)) continue;
                    string relative = RelativePath(targetRoot, oldFile);
                    if (relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                        relative.Equals("manifest.json.new", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!File.Exists(Path.Combine(sourceRoot, relative)))
                    {
                        var fi = new FileInfo(oldFile);
                        result.Files.Add(new SnapshotFileEntry
                        {
                            RelativePath = relative, Status = "orphaned",
                            SizeBytes = fi.Length, Sha256 = Sha256(oldFile),
                            ModifiedUtc = fi.LastWriteTimeUtc
                        });
                    }
                }

                timer.Stop();
                result.ElapsedMs = timer.ElapsedMilliseconds;
                // Write the manifest before the ready signal. Avoid publishing a partial JSON.
                string manifestPath = Path.Combine(targetRoot, "manifest.json");
                string candidate = manifestPath + ".new";
                File.WriteAllText(candidate, JsonConvert.SerializeObject(result, Formatting.Indented));
                if (File.Exists(manifestPath)) File.Replace(candidate, manifestPath, null);
                else File.Move(candidate, manifestPath);
                File.WriteAllText(ready, DateTime.UtcNow.ToString("o"));
            }
            finally
            {
                if (File.Exists(building)) File.Delete(building);
            }
        }

        private static SnapshotFileEntry CopyIfChanged(string source, string target, string relative,
            Dictionary<string, SnapshotFileEntry> previous)
        {
            var src = new FileInfo(source);
            if (!File.Exists(target))
            {
                File.Copy(source, target, true);
                File.SetLastWriteTimeUtc(target, src.LastWriteTimeUtc);
                return Entry(target, relative, Sha256(target), "new");
            }

            var dst = new FileInfo(target);
            bool same = src.Length == dst.Length && src.LastWriteTimeUtc == dst.LastWriteTimeUtc;
            SnapshotFileEntry prior;
            if (same && previous.TryGetValue(relative, out prior) &&
                !string.IsNullOrWhiteSpace(prior.Sha256))
                return Entry(target, relative, prior.Sha256, "unchanged");

            if (!same)
            {
                File.Copy(source, target, true);
                File.SetLastWriteTimeUtc(target, src.LastWriteTimeUtc);
                return Entry(target, relative, Sha256(target), "replaced");
            }
            return Entry(target, relative, Sha256(target), "unchanged_verified");
        }

        private static SnapshotFileEntry Entry(string path, string relative, string hash, string status)
        {
            var file = new FileInfo(path);
            return new SnapshotFileEntry
            {
                RelativePath = relative, Status = status, Sha256 = hash,
                SizeBytes = file.Length, ModifiedUtc = file.LastWriteTimeUtc
            };
        }

        private static Dictionary<string, SnapshotFileEntry> ReadPreviousManifest(string target)
        {
            var index = new Dictionary<string, SnapshotFileEntry>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(target, "manifest.json");
            if (!File.Exists(path)) return index;
            try
            {
                var last = JsonConvert.DeserializeObject<SnapshotResult>(File.ReadAllText(path));
                if (last?.Files != null)
                    foreach (var item in last.Files.Where(x => x != null && x.RelativePath != null))
                        index[item.RelativePath] = item;
            }
            catch { /* A corrupt/absent previous manifest forces hashing. */ }
            return index;
        }

        private static bool ShouldSkip(string file)
        {
            string name = Path.GetFileName(file);
            return name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".snapshot_building", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".snapshot_ready", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf(".partial.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RelativePath(string root, string file)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(file);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Path escaped root: " + file);
            return full.Substring(prefix.Length);
        }

        private static string Sha256(string file)
        {
            using (var stream = File.OpenRead(file))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    // Intentionally keeps the legacy JSON manifest property names and status labels.
    public sealed class SnapshotResult
    {
        public string SnapshotUuid { get; set; }
        public string InstanceUuid { get; set; }
        public string SystemUuid { get; set; }
        public string SystemName { get; set; }
        public string Fingerprint { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string SourceRoot { get; set; }
        public string SnapshotRoot { get; set; }
        public long ElapsedMs { get; set; }
        public List<SnapshotFileEntry> Files { get; set; } = new List<SnapshotFileEntry>();
    }

    public sealed class SnapshotFileEntry
    {
        public string RelativePath { get; set; }
        public string Status { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }
}
