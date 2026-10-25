using Godot;

namespace Refii.Ui;

/// <summary>
/// THE ROW ACTION'S PROMPT (<see cref="Engine_UiRowActions"/>): a button icon and a caption beside a
/// screen list's page buttons, placed off an anchor node of the owned book — left of the page
/// buttons' A, or below the abilities page's Learn — in the book's own scale, so it sits where the
/// shipped prompts sit on every window. Clicked when the pointer presses inside it.
/// </summary>
public partial class Engine_UiRowActionPrompt : Node3D
{
	/// <summary>Where the prompt stands from its anchor: beside a page button, or under a stacked one.</summary>
	public static Vector3 AnchorOffset(bool below) => below ? new(0, -40, 60) : new(-120, 0, 60);

	public void PlaceAt(Node3D anchor, bool below)
	{
		Transform3D transform = anchor.GlobalTransform;
		Vector3 scale = transform.Basis.Scale;
		Vector3 offset = AnchorOffset(below);
		GlobalPosition = transform.Origin + new Vector3(offset.X * scale.X, offset.Y * scale.Y, offset.Z);
		Scale = new Vector3(scale.X, scale.Y, 1);
	}

	private Engine_UiTheme? _theme;
	private MeshInstance3D? _label;
	private MeshInstance3D? _button;
	private string _buttonName = string.Empty;
	private string _text = string.Empty;
	private bool _onPage;

	/// <summary>
	/// Shows the prompt for <paramref name="button"/> (X or Y: the <c>x_img</c> / <c>y_img</c> markup
	/// icon) captioned <paramref name="text"/>, in the page's dark ink on a page or the HUD's pale ink
	/// off one. Repainted only on a change.
	/// </summary>
	public void SetState(Engine_UiTheme theme, string button, string text, bool onPage)
	{
		if (_label is null)
		{
			_label = theme.Label(string.Empty, 18, Colors.White, horizontalAlign: 2);
			_label.Name = "Text";
			_label.Position = new Vector3(-24, 1, 0);
			AddChild(_label);
			_button = new MeshInstance3D
			{
				Name = "Button", Mesh = new QuadMesh { Size = new(40, 40) },
				Position = new Vector3(1, -1, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			};
			AddChild(_button);
		}

		if (_theme != theme || _buttonName != button || _text != text || _onPage != onPage)
		{
			_theme = theme;
			_buttonName = button;
			_text = text;
			_onPage = onPage;
			string tag = button.ToLowerInvariant() + "_img";
			if (Engine_UiTextMarkup.Find(tag) is { } markup && theme.Textures?.Invoke(markup.Texture + ".tex") is { } art)
			{
				_button!.MaterialOverride = Engine_UiTheme.IconMaterial(art, 1f);
			}

			theme.Paint(_label, text, 18,
				onPage ? new Color(0.18f, 0.16f, 0.12f) : new Color(0.96f, 0.90f, 0.75f), horizontalAlign: 2);
		}

		Visible = true;
	}

	/// <summary>Whether the pointer pressed inside the prompt this frame (and the press is taken).</summary>
	public bool Clicked()
	{
		if (!Engine_UiPointer.LeftPressed || Engine_UiPointer.Canvas is not { } pointer ||
			_label is null || Engine_UiPointer.CanvasBounds(this) is not { } bounds)
		{
			return false;
		}

		return bounds.Grow(8).HasPoint(pointer) && Engine_UiPointer.TakeLeft();
	}
}
