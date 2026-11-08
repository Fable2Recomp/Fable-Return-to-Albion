using System;
using System.Collections.Generic;

using Godot;

using Refii.Textures;

namespace Refii.Titles.Fable2.PubGames;

/// <summary>The pub games' assets on a phone: the low-memory tier lets a left board's go.</summary>
public static partial class Engine_SwfAssets
{
    /// <summary>
    /// After the player has left a board, and its nodes are freed: lets go of what every board
    /// built (triangles and meshes, images and textures, parsed shapes, fonts), when
    /// <see cref="AndroidSwfCache.ReleasesBoards"/>. The next open reads them back
    /// from the cache folder. A board whose triangles are not on disk keeps them.
    /// </summary>
    public static void ReleaseBoards()
    {
        if (!AndroidSwfCache.ReleasesBoards)
        {
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<Game> games;
        List<Bitmap> bitmaps;
        List<Face> faces;
        lock (Gate)
        {
            games = new List<Game>(Games.Values);
            bitmaps = new List<Bitmap>(Bitmaps.Values);
            faces = new List<Face>(Faces.Values);
            foreach (FontPack pack in Packs)
            {
                lock (pack)
                {
                    pack.Document = null;
                }
            }
        }

        long bytes = 0;
        int meshes = 0;
        foreach (Game game in games)
        {
            lock (game)
            {
                SaveParts(game);
                if (game.PartsDirty)
                {
                    continue;
                }

                foreach (List<SwfVectorPart> parts in game.Parts.Values)
                {
                    foreach (SwfVectorPart part in parts)
                    {
                        bytes += AndroidSwfCache.PartBytes(
                            part.Vertices.Length, part.Colours is not null, part.Uvs is not null);
                        part.Mesh.Dispose();
                        part.Texture?.Dispose();
                        meshes++;
                    }
                }

                game.Parts.Clear();
                game.Cached = null;
                game.CacheRead = false;
                game.Document = null;
            }
        }

        foreach (Bitmap bitmap in bitmaps)
        {
            lock (bitmap)
            {
                foreach (Image? image in new[] { bitmap.Upload, bitmap.Enhanced, bitmap.Image })
                {
                    image?.Dispose();
                }

                bitmap.Placeholder?.Dispose();
                bitmap.Upload = bitmap.Enhanced = bitmap.Image = null;
                bitmap.Placeholder = null;
                bitmap.Png = null;
            }
        }

        foreach (Face face in faces)
        {
            lock (face)
            {
                face.Font?.Dispose();
                face.Font = null;
                face.Text = null;
            }
        }

        if (meshes == 0)
        {
            return;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
        GD.Print($"[swf] boards released: {meshes} meshes, {bytes / (1024 * 1024)} MB of triangles, " +
                 $"{clock.Elapsed.TotalMilliseconds:0} ms");
    }
}
