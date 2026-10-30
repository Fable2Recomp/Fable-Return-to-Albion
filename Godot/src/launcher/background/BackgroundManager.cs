using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// The launcher's background slideshow for one title: that title's art (<see cref="LauncherBackgrounds"/>)
/// faded between at random intervals, with each image's credit. A title with no art shows a plain
/// neutral background and no credit.
/// </summary>
public sealed class BackgroundManager
{
	private readonly LauncherBackgrounds? _source;
	private readonly List<Texture2D> _textures = new();
	private readonly List<string> _credits = new();
	private readonly RandomNumberGenerator _random = new();
	private readonly TextureRect _current;
	private readonly TextureRect _next;

	private float _timer;
	private int _currentIndex;
	private int _nextIndex;
	private float _transitionDuration;

	private readonly float _minimumDuration;
	private readonly float _maximumDuration;
	private readonly float _fadeDuration;

	public string CurrentCredit { get; private set; } = string.Empty;
	public float CurrentCreditAlpha { get; private set; }

	public BackgroundManager(TextureRect current, TextureRect next, LauncherBackgrounds? source)
	{
		_source = source;
		_current = current;
		_next = next;
		_minimumDuration = LauncherTheme.BackgroundMinimumDuration;
		_maximumDuration = LauncherTheme.BackgroundMaximumDuration;
		_fadeDuration = LauncherTheme.BackgroundFadeDuration;
		_random.Randomize();
		_transitionDuration = NextDuration();
	}

	public bool HasBackgrounds => _textures.Count > 0;

	private float NextDuration() =>
		_maximumDuration <= _minimumDuration
			? _minimumDuration
			: _random.RandfRange(_minimumDuration, _maximumDuration);

	private string CreditFor(string path)
	{
		string stem = Path.GetFileNameWithoutExtension(path);
		return _source is not null && _source.Credits.TryGetValue(stem, out string? credit) ? credit : string.Empty;
	}

	public void Initialize()
	{
		if (_source is not null)
		{
			LoadBundled(_source.BundledDirectory);
			if (_textures.Count == 0)
			{
				LoadExternal(_source.ExternalDirectory);
			}
		}

		_timer = 0.0f;
		_currentIndex = _textures.Count > 0 ? _random.RandiRange(0, _textures.Count - 1) : 0;
		_nextIndex = _textures.Count > 1 ? (_currentIndex + 1) % _textures.Count : _currentIndex;

		_current.Texture = _textures.Count > 0 ? _textures[_currentIndex] : NeutralBackground();
		_current.Modulate = Colors.White;
		_next.Texture = _textures.Count > 1 ? _textures[_nextIndex] : null;
		_next.Modulate = new Color(1, 1, 1, 0);

		CurrentCredit = _credits.Count > 0 ? _credits[_currentIndex] : string.Empty;
		CurrentCreditAlpha = CurrentCredit.Length == 0 ? 0.0f : 1.0f;
	}

	/// <summary>
	/// The plain background for a title with no art: the launcher's oak, darkening toward the foot
	/// where the action sits, so the cream text over it keeps its contrast.
	/// </summary>
	private static Texture2D NeutralBackground()
	{
		var gradient = new Gradient();
		gradient.SetColor(0, LauncherTokens.OakRaised);
		gradient.SetColor(1, new Color(LauncherTokens.Oak, 1.0f).Darkened(0.35f));
		return new GradientTexture2D
		{
			Gradient = gradient,
			Width = 4,
			Height = 256,
			FillFrom = new Vector2(0.5f, 0.0f),
			FillTo = new Vector2(0.5f, 1.0f),
		};
	}

	private void LoadBundled(string directory)
	{
		if (directory.Length == 0)
		{
			return;
		}

		using DirAccess? dir = DirAccess.Open(directory);
		if (dir is null)
		{
			return;
		}

		var names = new List<string>();
		dir.ListDirBegin();
		string entry = dir.GetNext();
		while (entry.Length != 0)
		{
			if (!dir.CurrentIsDir())
			{
				// An EXPORT packs the imported resource, not the source image: the
				// directory lists "foo.png.import" (and ".remap" where a resource was
				// remapped), while ResourceLoader still wants the original path. Stripping
				// the suffix is what makes this work in an exported build — rejecting
				// ".import" outright, as IsImage does, finds every background in the editor
				// and none once shipped.
				string name = entry;
				if (name.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
				{
					name = name[..^".import".Length];
				}
				else if (name.EndsWith(".remap", StringComparison.OrdinalIgnoreCase))
				{
					name = name[..^".remap".Length];
				}

				if (IsImage(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
				{
					names.Add(name);
				}
			}

			entry = dir.GetNext();
		}

		dir.ListDirEnd();
		names.Sort(StringComparer.OrdinalIgnoreCase);

		foreach (string name in names)
		{
			var texture = ResourceLoader.Load<Texture2D>(directory.TrimEnd('/') + "/" + name);
			if (texture is not null)
			{
				_textures.Add(texture);
				_credits.Add(CreditFor(name));
			}
		}
	}

	private void LoadExternal(string root)
	{
		if (root.Length == 0 || !Directory.Exists(root))
		{
			return;
		}

		string[] files = Directory.GetFiles(root);
		Array.Sort(files, StringComparer.OrdinalIgnoreCase);
		foreach (string file in files)
		{
			if (!IsImage(file))
			{
				continue;
			}

			Image image = Image.LoadFromFile(file);
			if (image is not null)
			{
				_textures.Add(ImageTexture.CreateFromImage(image));
				_credits.Add(CreditFor(file));
			}
		}
	}

	private static bool IsImage(string path)
	{
		string extension = Path.GetExtension(path).ToLowerInvariant();
		if (extension.EndsWith(".import", StringComparison.Ordinal))
		{
			return false;
		}

		return extension is ".png" or ".jpg" or ".jpeg" or ".bmp";
	}

	public void Update(float delta)
	{
		if (_textures.Count <= 1)
		{
			return;
		}

		_timer += delta;
		if (_timer >= _transitionDuration)
		{
			_timer -= _transitionDuration;
			_transitionDuration = NextDuration();
			_currentIndex = _nextIndex;
			_nextIndex = (_nextIndex + 1) % _textures.Count;

			_current.Texture = _textures[_currentIndex];
			_next.Texture = _textures[_nextIndex];
		}

		float fadeTime = _timer - (_transitionDuration - _fadeDuration);
		float alpha = 1.0f;
		bool inFade = false;

		if (fadeTime > 0.0f && _fadeDuration > 0.0f)
		{
			alpha = Mathf.Clamp(1.0f - (fadeTime / _fadeDuration), 0.0f, 1.0f);
			inFade = true;
		}

		float currentAlpha = inFade ? alpha : 1.0f;
		float nextAlpha = inFade ? 1.0f - alpha : 0.0f;

		_current.Modulate = new Color(1, 1, 1, currentAlpha);
		_next.Modulate = new Color(1, 1, 1, nextAlpha);

		string currentCredit = _credits[_currentIndex];
		string nextCredit = _credits[_nextIndex];

		if (currentCredit.Length != 0 && nextCredit.Length != 0)
		{
			bool nextIsDominant = nextAlpha > currentAlpha;
			CurrentCredit = nextIsDominant ? nextCredit : currentCredit;
			CurrentCreditAlpha = nextIsDominant ? nextAlpha : currentAlpha;
		}
		else if (nextCredit.Length != 0)
		{
			CurrentCredit = nextCredit;
			CurrentCreditAlpha = nextAlpha;
		}
		else if (currentCredit.Length != 0)
		{
			CurrentCredit = currentCredit;
			CurrentCreditAlpha = currentAlpha;
		}
		else
		{
			CurrentCredit = string.Empty;
			CurrentCreditAlpha = 0.0f;
		}
	}
}
