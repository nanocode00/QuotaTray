using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Svg.Skia;

namespace QuotaTray.Desktop.Services;

public static class ProviderIconService
{
    private static readonly ConcurrentDictionary<string, IImage?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Key, string[] Files)[] ProviderFileMappings = new[]
    {
        ("antigravity", new[] { "antigravity.png" }),
        ("codex", new[] { "codex.svg", "openai.svg" }),
        ("claude", new[] { "claude.svg", "claude.png" }),
        ("copilot", new[] { "copilot.svg", "copilot.png" }),
        ("openrouter", new[] { "openrouter.svg" }),
        ("kimi", new[] { "kimi.webp" }),
        ("zai", new[] { "zai.svg" }),
        ("synthetic", new[] { "synthetic.svg", "synthetic.png" }),
    };

    public static IImage? GetIcon(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return null;

        return _iconCache.GetOrAdd(providerKey, key =>
        {
            var cleanKey = key.ToLowerInvariant().Trim();

            string[] candidateFiles = Array.Empty<string>();
            foreach (var mapping in ProviderFileMappings)
            {
                if (cleanKey.Contains(mapping.Key, StringComparison.OrdinalIgnoreCase))
                {
                    candidateFiles = mapping.Files;
                    break;
                }
            }

            if (candidateFiles.Length == 0)
            {
                candidateFiles = new[] { $"{cleanKey}.svg", $"{cleanKey}.png", $"{cleanKey}.webp" };
            }

            foreach (var file in candidateFiles)
            {
                var uri = new Uri($"avares://QuotaTray.Desktop/Assets/Providers/{file}");
                try
                {
                    if (AssetLoader.Exists(uri))
                    {
                        using var stream = AssetLoader.Open(uri);
                        if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                        {
                            var source = SvgSource.Load(uri.ToString(), null);
                            if (source != null)
                            {
                                return new SvgImage { Source = source };
                            }
                        }
                        else
                        {
                            return new Bitmap(stream);
                        }
                    }
                }
                catch
                {
                    // Fallback to next candidate
                }
            }

            return null;
        });
    }
}
