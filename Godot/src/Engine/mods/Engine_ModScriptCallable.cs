using Godot;

namespace Refii.Mods;

/// <summary>
/// A SCRIPT'S CALLBACK AS C# CAN CALL IT. Godot's C# bridge carries only a method on an object across
/// to a <see cref="Callable"/>: a GDScript lambda arrives empty, and calling it does nothing and says
/// nothing. So a <see cref="RefiiHost"/> call that takes a callback takes it as a <see cref="Variant"/>,
/// which keeps the lambda whole, and <see cref="Of"/> hands back a method of a small script object
/// holding it. A method on an object is returned as it came.
/// </summary>
public static class ModScriptCallable
{
	private const string Source =
		"extends RefCounted\n" +
		"var inner\n" +
		"func run(...args):\n" +
		"\tif inner is Callable and inner.is_valid():\n" +
		"\t\treturn inner.callv(args)\n" +
		"\treturn null\n";

	private static GDScript? _holder;

	public static Callable Of(Variant callback)
	{
		Callable plain = callback.AsCallable();
		if (callback.VariantType != Variant.Type.Callable || plain.Target is not null || plain.Delegate is not null)
		{
			return plain;
		}

		if (_holder is null)
		{
			_holder = new GDScript { SourceCode = Source };
			_holder.Reload();
		}

		GodotObject holder = _holder.New().AsGodotObject();
		holder.Set("inner", callback);
		return new Callable(holder, "run");
	}
}
