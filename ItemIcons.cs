using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ExileCore;
using Newtonsoft.Json.Linq;

namespace SellMyShit
{
    // Item icons for the tooltip. poe.ninja's price files, which the Ninja Price plugin keeps on
    // disk, hold the CDN path of every currency icon, so no extra listing is downloaded: the names
    // and paths are read from there, and each icon PNG is fetched once, slowly and in the
    // background, into the "icons" folder next to the plugin.
    public sealed class ItemIcons
    {
        private const string CdnRoot = "https://web.poecdn.com";

        private static readonly string[] NinjaFiles =
        [
            "Currency2.json", "Essences2.json", "Fragments2.json", "DivinationCards2.json",
            "Fossils2.json", "Resonators2.json", "Oils2.json", "Scarabs2.json",
            "DeliriumOrbs2.json", "Artifacts2.json", "Tattoos2.json", "Omens2.json",
            "KalguuranRunes2.json", "AllflameEmbers2.json", "Astrolabe2.json", "Ducats.json",
            "Threads.json", "DjinnCoinsUrl2.json"
        ];

        private static readonly HttpClient Http = CreateClient();

        private readonly Graphics _graphics;
        private readonly string _iconDir;
        private readonly string _pluginDir;

        // name -> CDN path, filled once in the background
        private volatile Dictionary<string, string> _imageByName;
        private int _mapLoading;

        private readonly ConcurrentQueue<string> _downloadQueue = new();
        private readonly ConcurrentDictionary<string, byte> _requested =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, bool> _initialised =
            new(StringComparer.OrdinalIgnoreCase);

        private int _worker;

        public ItemIcons(Graphics graphics, string pluginDirectory)
        {
            _graphics = graphics;
            _pluginDir = pluginDirectory;
            _iconDir = Path.Combine(pluginDirectory, "icons");
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SellMyShit-icons");
            return client;
        }

        private static string FileNameFor(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".png";
        }

        // Call from the render thread. Returns false until the icon is downloaded and loaded.
        public bool TryGetTexture(string name, out IntPtr texture)
        {
            texture = IntPtr.Zero;

            if (string.IsNullOrWhiteSpace(name))
                return false;

            var path = Path.Combine(_iconDir, FileNameFor(name));

            if (File.Exists(path))
            {
                var key = "SellMyShit.icon." + name;

                if (!_initialised.TryGetValue(key, out var ok))
                {
                    ok = _graphics.InitImage(key, path);
                    _initialised[key] = ok;
                }

                if (ok && _graphics.HasImage(key))
                {
                    texture = _graphics.GetTextureId(key);
                    return texture != IntPtr.Zero;
                }

                return false;
            }

            Request(name);
            return false;
        }

        private void Request(string name)
        {
            var map = _imageByName;

            if (map == null)
            {
                if (System.Threading.Interlocked.CompareExchange(ref _mapLoading, 1, 0) == 0)
                    Task.Run(LoadMap);

                return;
            }

            if (!map.ContainsKey(name) || !_requested.TryAdd(name, 0))
                return;

            _downloadQueue.Enqueue(name);

            if (System.Threading.Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
                Task.Run(DownloadLoop);
        }

        private void LoadMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var ninjaDir = FindNinjaLeagueDirectory();

                if (ninjaDir != null)
                {
                    foreach (var file in NinjaFiles)
                    {
                        var path = Path.Combine(ninjaDir, file);

                        if (!File.Exists(path))
                            continue;

                        var items = JObject.Parse(File.ReadAllText(path))["items"] as JArray;

                        if (items == null)
                            continue;

                        foreach (var item in items)
                        {
                            var name = (string)item["name"];
                            var image = (string)item["image"];

                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(image))
                                map.TryAdd(name, image);
                        }
                    }
                }
            }
            catch
            {
                // no icons then; the tooltip just stays text only
            }

            _imageByName = map;
        }

        // the newest league folder of the Ninja Price plugin's NinjaData, wherever the plugin
        // folder is called
        private string FindNinjaLeagueDirectory()
        {
            var plugins = Directory.GetParent(_pluginDir)?.FullName;

            if (plugins == null)
                return null;

            return Directory
                .EnumerateDirectories(plugins)
                .Select(dir => Path.Combine(dir, "NinjaData"))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .Where(dir => File.Exists(Path.Combine(dir, "Currency2.json")))
                .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "Currency2.json")))
                .FirstOrDefault();
        }

        private async Task DownloadLoop()
        {
            try
            {
                Directory.CreateDirectory(_iconDir);

                while (_downloadQueue.TryDequeue(out var name))
                {
                    try
                    {
                        if (_imageByName.TryGetValue(name, out var image))
                        {
                            var target = Path.Combine(_iconDir, FileNameFor(name));
                            var bytes = await Http.GetByteArrayAsync(CdnRoot + image);

                            // write under a temp name so a half file is never picked up
                            var temp = target + ".tmp";
                            await File.WriteAllBytesAsync(temp, bytes);
                            File.Move(temp, target, true);
                        }
                    }
                    catch
                    {
                        // a failed icon is simply not shown; it is not retried this session
                    }

                    await Task.Delay(250);
                }
            }
            finally
            {
                System.Threading.Volatile.Write(ref _worker, 0);

                // something may have been queued just as the loop ended
                if (!_downloadQueue.IsEmpty &&
                    System.Threading.Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
                {
                    _ = Task.Run(DownloadLoop);
                }
            }
        }
    }
}
