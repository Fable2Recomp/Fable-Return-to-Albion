namespace Refii.Tests.Audio;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Audio;
using Refii.Bnk;
using Refii.Core;
using Refii.Models;

[TestSuite]
public class WarmupMusicTests
{
    [TestCase]
    public void TestMenuMusicPathMatchesConvention()
    {
        AssertThat(Engine_ShaderWarmup.MenuMusicPath).IsEqual("audio/music/menu_interlude.wav");
        AssertThat(Engine_BnkAudioLoader.Recognises("game://" + Engine_ShaderWarmup.MenuMusicPath)).IsTrue();
        AssertThat(Engine_BnkAudioLoader.Recognises("game://other/path.wav")).IsTrue();
        AssertThat(Engine_BnkAudioLoader.Recognises("res://other/path.wav")).IsFalse();
        AssertThat(Engine_BnkAudioLoader.Recognises("game://other/path.ogg")).IsFalse();
    }

    [TestCase]
    public void TestBnkFileSystemMountHasMenuInterlude()
    {
        string dataRoot = @"F:\Documents\programming\refii\Godot\build\x86_64\assets\game";
        if (!Directory.Exists(dataRoot))
        {
            dataRoot = Engine_GameData.Resolve();
        }

        if (string.IsNullOrEmpty(dataRoot) || !Directory.Exists(dataRoot))
        {
            return;
        }

        string musicBnk = Path.Combine(dataRoot, "data", "audio", "music.bnk");
        if (!File.Exists(musicBnk))
        {
            musicBnk = Path.Combine(dataRoot, "audio", "music.bnk");
        }

        AssertThat(File.Exists(musicBnk)).IsTrue();

        bool opened = BnkArchive.TryOpen(musicBnk, out BnkArchive? archive, out string error);
        AssertThat(opened).IsTrue();
        AssertThat(archive).IsNotNull();

        BnkEntry? match = null;
        foreach (var entry in archive!.Entries)
        {
            if (entry.RelativePath.Contains("menu_interlude", StringComparison.OrdinalIgnoreCase))
            {
                match = entry;
                break;
            }
        }

        AssertThat(match).IsNotNull();
        System.Console.WriteLine($"Found entry: {match!.RelativePath}, size: {match.Size}");
        AssertThat(match.RelativePath).IsEqual(Engine_ShaderWarmup.MenuMusicPath);

        byte[] raw = archive.ReadEntryBytes(match);
        AssertThat(raw.Length).IsGreater(0);

        bool decoded = Engine_XmaDecoder.TryDecode(
            raw, out short[] pcm, out int sampleRate, out int channels, out string xmaError);
        System.Console.WriteLine($"Decoded: {decoded}, samples: {pcm?.Length}, rate: {sampleRate}, channels: {channels}, error: {xmaError}");
        AssertThat(decoded).IsTrue();
        AssertThat(pcm).IsNotNull();
        AssertThat(pcm!.Length).IsGreater(0);
        AssertThat(sampleRate).IsEqual(48000);
        AssertThat(channels).IsEqual(2);

        int sampleCount = pcm.Length / channels;
        AssertThat(sampleCount).IsGreater(0);
        float durationSeconds = sampleCount / (float)sampleRate;
        AssertThat(durationSeconds).IsGreater(150.0f);
    }
}
