// Jiji — files under Jiji:StoragePath: {contentId}/r{revision}/… for one approved
// revision's narration, timings and renders. Every path is resolved and refused if it
// lands outside the root, so no id, name or ".." can reach another folder.

using Microsoft.Extensions.Options;

namespace RabbitIC.Api.Jiji.Services;

public sealed class JijiStorage(IOptions<JijiOptions> options)
{
    public string Root => options.Value.StorageRoot;

    /// <summary>{root}/{contentId}/r{revision}, created.</summary>
    public string RevisionDir(int contentId, int revision) =>
        Directory.CreateDirectory(Resolve($"{contentId}/r{revision}")).FullName;

    /// <summary>A path inside the root, or an exception. <paramref name="relative"/> uses '/'.</summary>
    public string Resolve(string relative)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException($"Refused a path outside Jiji:StoragePath: {relative}");
        return full;
    }

    /// <summary>The same check for a path built elsewhere (FFmpeg's inputs and outputs, day 4).</summary>
    public string Inside(string path) =>
        Resolve(Path.GetRelativePath(Path.GetFullPath(Root), Path.GetFullPath(path)).Replace(Path.DirectorySeparatorChar, '/'));
}
