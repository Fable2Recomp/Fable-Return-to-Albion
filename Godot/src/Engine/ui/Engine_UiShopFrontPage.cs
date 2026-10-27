using System.Collections.Generic;
using System.Globalization;

namespace Refii.Ui;

/// <summary>
/// THE SHOP'S FRONT PAGE (screen 22), composed from the guest's own answers
/// (<see cref="Refii.Host.HostBridge.GetShopFront"/>) by its writer's templates — `sub_82991800`,
/// verified, run once at the screen's open. Under `Foreground.FrontPage.Contents.`:
/// <list type="bullet">
/// <item>`ShopName` the shop's name key, or for a travelling trader TOWN_CRIER / TRAVELING_TRADER
/// (and `Group.EconomyDescription`, the anecdote, hidden); `StarRating` slide `2·trunc(stars)`.</item>
/// <item>`Group.Wealth.ShopWealthValue` `"%.1f"((v−1)·100) + "% " + MARK_UP` when v &gt; 1, else
/// the same of `(1−v)` with DISCOUNT.</item>
/// <item>`Group.OwnerKeeper` slide 2 for the owner (the hero's title, `ShopOwnersDiscountValue`
/// `|int((1−x)·100)|%`; opinions and special reward hidden), slide 1 otherwise (the owner
/// discount hidden; Love/Hate/Fear `|int((1−x)·100)|% ` + DISCOUNT/MARK_UP/DISCOUNT; the special
/// reward `int((1−x)·100)%` while x &lt; 1, hidden otherwise).</item>
/// <item>`Group.SaleShortage` from the sale factor: pct = trunc((f−1)·100); a sale (pct &lt; 0)
/// SALE and `pct%`, a shortage (pct &gt; 0) SHORTAGE and `+pct%`, either with DAY/DAYS `%1` →
/// days; hidden otherwise.</item>
/// </list>
/// </summary>
public static class Engine_UiShopFrontPage
{
	private const string Page = "Foreground.FrontPage.Contents.";

	private const string Tag = "GUI_SCREEN_SHOP_FRONT_PAGE_";

	public sealed class Statements
	{
		public readonly Dictionary<string, string> Values = new();
		public readonly Dictionary<string, bool> Active = new();
		public readonly Dictionary<string, int> Slides = new();
	}

	private static ulong _frame = ulong.MaxValue;

	private static Statements? _said;

	/// <summary>The page's statements, once a frame; null while the guest has answered nothing.</summary>
	public static Statements? State(Refii.Text.Engine_BabelDatabase? strings)
	{
		ulong frame = Godot.Engine.GetProcessFrames();
		if (frame != _frame)
		{
			_frame = frame;
			_said = Compute(Refii.Host.HostBridge.GetShopFront(), strings);
		}

		return _said;
	}

	/// <summary>`sub_8230E0E8`: the key, then `TEXT_` + key, then the key itself.</summary>
	private static string Loc(Refii.Text.Engine_BabelDatabase? strings, string key)
	{
		if (key.Length == 0)
		{
			return string.Empty;
		}

		if (strings is not null)
		{
			if (strings.TryGetString(key, out string text) && text.Length != 0)
			{
				return text;
			}

			if (strings.TryGetString("TEXT_" + key, out text) && text.Length != 0)
			{
				return text;
			}
		}

		return key;
	}

	private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

	private static int Percent(float x) => (int)((1f - x) * 100f);

	private static Statements? Compute(Refii.Host.HostBridge.ShopFront s, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (!s.Known)
		{
			return null;
		}

		var said = new Statements();
		if (s.Trader)
		{
			said.Values[Page + "ShopName"] = Loc(strings, s.Crier
				? "TEXT_CHARACTER_OCCUPATION_TOWN_CRIER"
				: Tag + "TRAVELING_TRADER");
			said.Active[Page + "Group.EconomyDescription"] = false;
		}
		else
		{
			said.Values[Page + "ShopName"] = Loc(strings, s.NameKey);
			said.Values[Page + "Group.EconomyDescription"] = Loc(strings, s.AnecdoteKey);
		}

		said.Slides[Page + "StarRating"] = 2 * (int)System.MathF.Truncate(s.Stars);

		// `"%.1f"` with the trailing-zero strip that leaves one decimal.
		float wealth = s.Wealth;
		bool markUp = wealth > 1f;
		float amount = markUp ? (wealth - 1f) * 100f : (1f - wealth) * 100f;
		said.Values[Page + "Group.Wealth.ShopWealthValue"] =
			amount.ToString("0.0", CultureInfo.InvariantCulture) + "% " +
			Loc(strings, Tag + (markUp ? "MARK_UP" : "DISCOUNT"));

		if (s.HasKeeperName)
		{
			said.Values[Page + "Group.OwnerKeeper.ShopKeeperValue"] = s.KeeperName;
		}

		if (s.Owner)
		{
			said.Slides[Page + "Group.OwnerKeeper"] = 2;
			if (s.TitleKey.Length != 0)
			{
				said.Values[Page + "Group.OwnerKeeper.ShopOwnerValue"] = Loc(strings, s.TitleKey);
			}

			said.Active[Page + "Group.ShopkeeperOpinions"] = false;
			said.Active[Page + "Group.SpecialReward"] = false;
			said.Values[Page + "Group.ShopOwnerDiscount.ShopOwnersDiscountValue"] =
				Int(System.Math.Abs(Percent(s.OwnerDiscount))) + "%";
		}
		else
		{
			said.Slides[Page + "Group.OwnerKeeper"] = 1;
			said.Active[Page + "Group.ShopOwnerDiscount"] = false;
			(string Path, float Value, string Kind)[] opinions =
			{
				("LoveValue", s.Love, "DISCOUNT"), ("HateValue", s.Hate, "MARK_UP"), ("FearValue", s.Fear, "DISCOUNT"),
			};
			foreach ((string path, float value, string kind) in opinions)
			{
				said.Values[Page + "Group.ShopkeeperOpinions." + path] =
					Int(System.Math.Abs(Percent(value))) + "% " + Loc(strings, Tag + kind);
			}

			if (s.Special < 1f)
			{
				said.Values[Page + "Group.SpecialReward.SpecialRewardDiscountValue"] = Int(Percent(s.Special)) + "%";
			}
			else
			{
				said.Active[Page + "Group.SpecialReward"] = false;
			}
		}

		int pct = (int)((1f - s.SaleFactor) * -100f);
		if (s.SaleKnown && pct != 0)
		{
			said.Values[Page + "Group.SaleShortage.Sale"] = Loc(strings, Tag + (pct < 0 ? "SALE" : "SHORTAGE"));
			said.Values[Page + "Group.SaleShortage.PricesValue"] = (pct > 0 ? "+" : string.Empty) + Int(pct) + "%";
			said.Values[Page + "Group.SaleShortage.TimeRemainingValue"] =
				Loc(strings, Tag + (s.SaleDays == 1 ? "DAY" : "DAYS"))
					.Replace("%1", Int(s.SaleDays), System.StringComparison.Ordinal);
		}
		else
		{
			said.Active[Page + "Group.SaleShortage"] = false;
		}

		return said;
	}
}
