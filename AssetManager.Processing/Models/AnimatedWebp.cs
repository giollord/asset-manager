using ImageMagick;

namespace AssetManager.Processing.Models;

/// <summary>Joins rendered clip frames into one looping, lossy animated WebP (keeps transparency).</summary>
public static class AnimatedWebp
{
    public static byte[] Encode(AnimationFrames frames, int quality)
    {
        if (frames.Frames.Count == 0) throw new ArgumentException("No frames.", nameof(frames));
        using var collection = new MagickImageCollection();
        var delay = (uint)Math.Max(1, Math.Round(100.0 / Math.Max(1, frames.Fps))); // in 1/100 s
        foreach (var bytes in frames.Frames)
        {
            var frame = new MagickImage(bytes);
            frame.AnimationDelay = delay;
            frame.GifDisposeMethod = GifDisposeMethod.Background; // transparent frames must not pile up
            collection.Add(frame);
        }
        collection[0].AnimationIterations = 0; // loop forever
        collection[0].Settings.SetDefine(MagickFormat.WebP, "lossless", false);
        collection[0].Quality = (uint)Math.Clamp(quality, 1, 100);
        foreach (var f in collection) f.Quality = (uint)Math.Clamp(quality, 1, 100);
        using var output = new MemoryStream();
        collection.Write(output, MagickFormat.WebP);
        return output.ToArray();
    }
}
