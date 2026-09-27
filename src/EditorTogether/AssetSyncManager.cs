using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI;
using UnityModManagerNet;

namespace EditorTogether
{
    internal sealed class AssetSyncManager : IDisposable
    {
        private readonly UnityModManager.ModEntry.ModLogger logger;
        private readonly WebSocketTransport transport;
        private readonly HttpClient http = new HttpClient();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ConcurrentQueue<string> completedLevels = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<string, byte> readyLevels = new ConcurrentDictionary<string, byte>();
        private readonly string rootDirectory;
        private readonly string contentCacheDirectory;
        private readonly string sessionDirectory;
        private string latestHostLevelId = string.Empty;
        private string latestRemoteLevelId = string.Empty;
        private string status = "Idle";

        public string Status => status;

        public AssetSyncManager(UnityModManager.ModEntry.ModLogger logger, WebSocketTransport transport)
        {
            this.logger = logger;
            this.transport = transport;
            rootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EditorTogether");
            contentCacheDirectory = Path.Combine(rootDirectory, "AssetCache");
            sessionDirectory = Path.Combine(rootDirectory, "Sessions");
            Directory.CreateDirectory(contentCacheDirectory);
            Directory.CreateDirectory(sessionDirectory);
            http.Timeout = TimeSpan.FromMinutes(10);
        }

        public async Task PublishHostManifestAsync(string levelId, LevelData levelData, string levelPath)
        {
            if (string.IsNullOrEmpty(levelId) || levelData == null || string.IsNullOrEmpty(levelPath)) return;
            latestHostLevelId = levelId;

            string levelRoot = Path.GetDirectoryName(levelPath);
            if (string.IsNullOrEmpty(levelRoot) || !Directory.Exists(levelRoot))
            {
                status = "Assets: level folder unavailable";
                return;
            }

            List<string> references = CollectReferencedAssets(levelData);
            if (references.Count == 0)
            {
                status = "Assets: none";
                if (latestHostLevelId == levelId && transport.IsConnected)
                    await transport.SendAssetManifestAsync(levelId, Array.Empty<AssetDescriptor>()).ConfigureAwait(false);
                return;
            }

            try
            {
                var assets = new List<AssetDescriptor>();
                int processed = 0;
                foreach (string relativePath in references)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (latestHostLevelId != levelId) return;

                    if (!TryResolveInside(levelRoot, relativePath, out string fullPath) || !File.Exists(fullPath))
                    {
                        logger.Warning("[CollabAssets] missing or unsafe asset path: " + relativePath);
                        continue;
                    }

                    status = $"Assets: hashing {processed + 1}/{references.Count}";
                    AssetDescriptor descriptor = await Task.Run(() => CreateDescriptor(relativePath, fullPath), cancellation.Token).ConfigureAwait(false);
                    assets.Add(descriptor);
                    processed++;
                }

                for (int i = 0; i < assets.Count; i++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (latestHostLevelId != levelId) return;
                    AssetDescriptor asset = assets[i];
                    string fullPath;
                    if (!TryResolveInside(levelRoot, asset.Path, out fullPath) || !File.Exists(fullPath)) continue;
                    status = $"Assets: uploading {i + 1}/{assets.Count}";
                    await EnsureUploadedAsync(asset, fullPath, cancellation.Token).ConfigureAwait(false);
                }

                if (latestHostLevelId != levelId || !transport.IsConnected) return;
                await transport.SendAssetManifestAsync(levelId, assets).ConfigureAwait(false);
                status = $"Assets: ready ({assets.Count})";
                logger.Log($"[CollabAssets] manifest published; level={levelId}, assets={assets.Count}, bytes={assets.Sum(x => x.Size)}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status = "Assets: upload failed";
                logger.Error("[CollabAssets] host asset sync failed: " + ex);
            }
        }

        public void QueueRemoteManifest(AssetManifestMessage manifest)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.LevelId)) return;
            latestRemoteLevelId = manifest.LevelId;
            readyLevels.TryRemove(manifest.LevelId, out _);
            _ = DownloadManifestAsync(manifest);
        }

        private async Task DownloadManifestAsync(AssetManifestMessage manifest)
        {
            try
            {
                AssetDescriptor[] assets = manifest.Assets ?? Array.Empty<AssetDescriptor>();
                if (assets.Length == 0)
                {
                    status = "Assets: none";
                    readyLevels[manifest.LevelId] = 1;
                    completedLevels.Enqueue(manifest.LevelId);
                    return;
                }

                string levelSessionDirectory = GetSessionDirectory(manifest.LevelId);
                Directory.CreateDirectory(levelSessionDirectory);

                for (int i = 0; i < assets.Length; i++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (latestRemoteLevelId != manifest.LevelId) return;

                    AssetDescriptor asset = assets[i];
                    if (!IsSha256(asset.Hash) || !TryResolveInside(levelSessionDirectory, asset.Path, out string destinationPath))
                    {
                        logger.Warning("[CollabAssets] ignored unsafe manifest asset: " + asset.Path);
                        continue;
                    }

                    status = $"Assets: downloading {i + 1}/{assets.Length}";
                    string cachePath = Path.Combine(contentCacheDirectory, asset.Hash.ToLowerInvariant());
                    if (!await IsCachedAssetValidAsync(cachePath, asset, cancellation.Token).ConfigureAwait(false))
                        await DownloadAssetAsync(asset, cachePath, cancellation.Token).ConfigureAwait(false);

                    string destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
                    File.Copy(cachePath, destinationPath, true);
                }

                if (latestRemoteLevelId != manifest.LevelId) return;
                status = $"Assets: ready ({assets.Length})";
                readyLevels[manifest.LevelId] = 1;
                completedLevels.Enqueue(manifest.LevelId);
                logger.Log($"[CollabAssets] assets ready; level={manifest.LevelId}, assets={assets.Length}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status = "Assets: download failed";
                logger.Error("[CollabAssets] client asset sync failed: " + ex);
            }
        }

        public bool TryDequeueCompleted(out string levelId) => completedLevels.TryDequeue(out levelId);
        public bool IsLevelReady(string levelId) => !string.IsNullOrEmpty(levelId) && readyLevels.ContainsKey(levelId);

        public string PrepareRemoteLevel(string levelId, string encodedLevel)
        {
            string directory = GetSessionDirectory(levelId);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "main.adofai");
            File.WriteAllText(path, encodedLevel ?? string.Empty);
            return path;
        }

        public void Reset()
        {
            latestHostLevelId = string.Empty;
            latestRemoteLevelId = string.Empty;
            status = "Idle";
            readyLevels.Clear();
            while (completedLevels.TryDequeue(out _)) { }
        }

        private string GetSessionDirectory(string levelId)
        {
            string safe = string.IsNullOrEmpty(levelId) ? "unknown" : new string(levelId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            if (string.IsNullOrEmpty(safe)) safe = "unknown";
            return Path.Combine(sessionDirectory, safe);
        }

        private async Task EnsureUploadedAsync(AssetDescriptor asset, string fullPath, CancellationToken token)
        {
            Uri baseUri = transport.HttpBaseUri;
            if (baseUri == null) throw new InvalidOperationException("HTTP asset endpoint is unavailable before WebSocket connection.");
            Uri uri = new Uri(baseUri, "assets/" + asset.Hash.ToLowerInvariant());

            using (var head = new HttpRequestMessage(HttpMethod.Head, uri))
            using (HttpResponseMessage headResponse = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                if (headResponse.StatusCode == HttpStatusCode.OK) return;
                if (headResponse.StatusCode != HttpStatusCode.NotFound)
                    headResponse.EnsureSuccessStatusCode();
            }

            using (var request = new HttpRequestMessage(HttpMethod.Put, uri))
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            using (var content = new StreamContent(stream, 128 * 1024))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentLength = asset.Size;
                request.Content = content;
                using (HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    response.EnsureSuccessStatusCode();
            }
        }

        private async Task DownloadAssetAsync(AssetDescriptor asset, string cachePath, CancellationToken token)
        {
            Uri baseUri = transport.HttpBaseUri;
            if (baseUri == null) throw new InvalidOperationException("HTTP asset endpoint is unavailable before WebSocket connection.");
            Uri uri = new Uri(baseUri, "assets/" + asset.Hash.ToLowerInvariant());
            string tempPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                using (HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                        await input.CopyToAsync(output, 128 * 1024, token).ConfigureAwait(false);
                }

                var downloaded = new FileInfo(tempPath);
                if (downloaded.Length != asset.Size) throw new InvalidDataException("Downloaded asset size mismatch for " + asset.Path);
                string actualHash = await Task.Run(() => ComputeSha256(tempPath), token).ConfigureAwait(false);
                if (!string.Equals(actualHash, asset.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded asset hash mismatch for " + asset.Path);

                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(tempPath, cachePath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static async Task<bool> IsCachedAssetValidAsync(string cachePath, AssetDescriptor asset, CancellationToken token)
        {
            if (!File.Exists(cachePath)) return false;
            if (new FileInfo(cachePath).Length != asset.Size) return false;
            string hash = await Task.Run(() => ComputeSha256(cachePath), token).ConfigureAwait(false);
            return string.Equals(hash, asset.Hash, StringComparison.OrdinalIgnoreCase);
        }

        private static AssetDescriptor CreateDescriptor(string relativePath, string fullPath)
        {
            var info = new FileInfo(fullPath);
            return new AssetDescriptor(NormalizeRelativePath(relativePath), ComputeSha256(fullPath), info.Length);
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, false))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static List<string> CollectReferencedAssets(LevelData data)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPath(paths, data.previewImage);
            AddPath(paths, data.previewIcon);
            AddPath(paths, data.artistPermission);
            AddPath(paths, data.bgImage);
            AddPath(paths, data.songFilename);
            AddPath(paths, data.bgVideo);

            if (data.levelEvents != null)
            {
                foreach (LevelEvent e in data.levelEvents)
                {
                    if (e == null) continue;
                    if (e.eventType == LevelEventType.CustomBackground) AddPath(paths, e.GetString("bgImage"));
                    else if (e.eventType == LevelEventType.ColorTrack) AddPath(paths, e.GetString("trackTexture"));
                    else if (e.eventType == LevelEventType.MoveDecorations) AddPath(paths, e.GetString("decorationImage"));
                }
            }

            if (data.decorations != null)
            {
                foreach (LevelEvent e in data.decorations)
                {
                    if (e == null) continue;
                    if (e.eventType == LevelEventType.AddDecoration || e.eventType == LevelEventType.AddParticle)
                        AddPath(paths, e.GetString("decorationImage"));
                }
            }

            return paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddPath(HashSet<string> paths, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string normalized = NormalizeRelativePath(value.Trim());
            if (normalized.Contains("://")) return;
            if (Path.IsPathRooted(normalized)) return;
            paths.Add(normalized);
        }

        private static string NormalizeRelativePath(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private static bool TryResolveInside(string root, string relativePath, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath)) return false;
            if (Path.IsPathRooted(relativePath)) return false;

            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = candidate;
            return true;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        public void Dispose()
        {
            try { cancellation.Cancel(); } catch { }
            cancellation.Dispose();
            http.Dispose();
        }
    }

    internal sealed class AssetDescriptor
    {
        public string Path { get; }
        public string Hash { get; }
        public long Size { get; }

        public AssetDescriptor(string path, string hash, long size)
        {
            Path = path ?? string.Empty;
            Hash = hash ?? string.Empty;
            Size = size;
        }
    }

    internal sealed class AssetManifestMessage
    {
        public string ClientId { get; }
        public string LevelId { get; }
        public AssetDescriptor[] Assets { get; }

        public AssetManifestMessage(string clientId, string levelId, AssetDescriptor[] assets)
        {
            ClientId = clientId ?? string.Empty;
            LevelId = levelId ?? string.Empty;
            Assets = assets ?? Array.Empty<AssetDescriptor>();
        }
    }
}
