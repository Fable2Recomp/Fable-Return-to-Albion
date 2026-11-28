using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.Mods;
using Refii.Titles.Fable2;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests.Ui;

/// <summary>
/// Unit and feature tests for screen 28 (<c>RealEstateMenu</c>): registered <c>estate.*</c> row
/// conditions, property state matrix, <see cref="Engine_UiRealEstatePage.ConditionKey"/> transitions,
/// and <see cref="Engine_UiScreenMenu.InvalidateAuthoredIfChanged"/> lifecycle.
/// </summary>
[TestSuite]
public sealed class RealEstateMenuTests
{
	private const uint EstatePresentation = 0x4A002800u;

	private static Fable2Bridge.RealEstate Estate(
		bool known = true,
		bool owned = false,
		bool hasTenant = false,
		uint typeMask = 1u) =>
		new(
			Known: known,
			Record: 0x1234u,
			TypeMask: typeMask,
			Owned: owned,
			HasTenant: hasTenant,
			Value: 1000,
			Economy: 0,
			Decoration: 0,
			Income: 50,
			SalePrice: 1200,
			SellingFees: 100,
			NotVacant: 0,
			OwnerKilled: 0,
			HeroOpinion: 0,
			BonusesRecord: 0u,
			Occupant: string.Empty,
			TitleRecord: 0u);

	private static Dictionary<string, bool> Facts(in Fable2Bridge.RealEstate estate) => new(StringComparer.Ordinal)
	{
		[Engine_UiRowConditions.EstateKnown] = estate.Known,
		[Engine_UiRowConditions.EstateOwned] = estate.Known && estate.Owned,
		[Engine_UiRowConditions.EstateVacant] = estate.Known && !estate.HasTenant,
		[Engine_UiRowConditions.EstateTenanted] = estate.Known && estate.HasTenant,
		[Engine_UiRowConditions.EstateBusiness] = estate.Known && estate.IsBusiness,
		[Engine_UiRowConditions.EstateHome] = estate.Known && !estate.IsBusiness,
	};

	/// <summary>
	/// Every <c>estate.*</c> condition constant is registered in <see cref="Engine_UiRowConditions.Game"/>
	/// and the composed game templates validate with zero unknown-condition warnings.
	/// </summary>
	[TestCase]
	public void EstateConditionsAreRegisteredAndTemplatesValidateWithoutWarnings()
	{
		string[] expected =
		{
			Engine_UiRowConditions.EstateKnown,
			Engine_UiRowConditions.EstateOwned,
			Engine_UiRowConditions.EstateVacant,
			Engine_UiRowConditions.EstateTenanted,
			Engine_UiRowConditions.EstateBusiness,
			Engine_UiRowConditions.EstateHome,
		};

		foreach (string name in expected)
		{
			AssertThat(Engine_UiRowConditions.Game.Contains(name)).OverrideFailureMessage($"missing {name}").IsTrue();
		}

		List<ModIssue> issues = ModValidate.Validate(Engine_UiTemplates.ModTemplatesPath, Engine_UiGameTemplates.Text).ToList();
		AssertThat(issues.Any(i => i.Message.Contains("is not a condition the game states", StringComparison.Ordinal)))
			.OverrideFailureMessage("expected no unknown-condition warnings:\n" + string.Join("\n", issues))
			.IsFalse();
	}

	/// <summary>
	/// Evaluates <c>RealEstateMenu</c> across every property state (unknown, unowned, owned vacant home,
	/// owned tenanted home, and owned business) to ensure only valid actions are built.
	/// </summary>
	[TestCase]
	public void RealEstateMenuStateMatrixEmitsOnlyApplicableActions()
	{
		Engine_UiMenuTemplate menu = Engine_UiTemplates.ComposeGame().Menus["RealEstateMenu"];

		static string RowNames(Engine_UiMenuTemplate template, in Fable2Bridge.RealEstate estate)
		{
			var built = new Engine_FrontEndMenu();
			template.BuildInto(built, committable: true, Facts(estate));
			return string.Join(",", built.Visible.Select(r => r.Name));
		}

		// Unknown building before guest answers: Buy must not flash.
		AssertThat(RowNames(menu, Estate(known: false, owned: false))).IsEqual(string.Empty);

		// Unowned home or business: Buy only.
		AssertThat(RowNames(menu, Estate(known: true, owned: false, typeMask: 1u)))
			.IsEqual("GUI_SCREEN_REAL_ESTATE_BUY");
		AssertThat(RowNames(menu, Estate(known: true, owned: false, typeMask: 0x020u)))
			.IsEqual("GUI_SCREEN_REAL_ESTATE_BUY");

		// Owned vacant home: Sell, Rent Out, Set Budget.
		AssertThat(RowNames(menu, Estate(known: true, owned: true, hasTenant: false, typeMask: 1u)))
			.IsEqual("GUI_SCREEN_REAL_ESTATE_SELL,GUI_SCREEN_REAL_ESTATE_RENT_OUT,GUI_SCREEN_REAL_ESTATE_SET_BUDGET");

		// Owned tenanted home: Sell, Evict Tenant, Adjust Rent (no Rent Out or Set Budget).
		AssertThat(RowNames(menu, Estate(known: true, owned: true, hasTenant: true, typeMask: 1u)))
			.IsEqual("GUI_SCREEN_REAL_ESTATE_SELL,GUI_SCREEN_REAL_ESTATE_EVICT_TENNANT,GUI_SCREEN_REAL_ESTATE_ADJUST_RENT");

		// Owned business: Sell, Adjust Profits (no Rent Out or Set Budget).
		AssertThat(RowNames(menu, Estate(known: true, owned: true, hasTenant: false, typeMask: 0x020u)))
			.IsEqual("GUI_SCREEN_REAL_ESTATE_SELL,GUI_SCREEN_REAL_ESTATE_ADJUST_PROFITS");
	}

	/// <summary>
	/// <see cref="Engine_UiRealEstatePage.ConditionKey"/> distinguishes every condition-relevant property state,
	/// including owned vacant home vs. owned business.
	/// </summary>
	[TestCase]
	public void ConditionKeyDistinguishesHomeAndBusinessTransitions()
	{
		int unknown = Engine_UiRealEstatePage.ConditionKey(Estate(known: false));
		int unownedHome = Engine_UiRealEstatePage.ConditionKey(Estate(known: true, owned: false, typeMask: 1u));
		int unownedBusiness = Engine_UiRealEstatePage.ConditionKey(Estate(known: true, owned: false, typeMask: 0x020u));
		int ownedVacantHome = Engine_UiRealEstatePage.ConditionKey(Estate(known: true, owned: true, hasTenant: false, typeMask: 1u));
		int ownedTenantedHome = Engine_UiRealEstatePage.ConditionKey(Estate(known: true, owned: true, hasTenant: true, typeMask: 1u));
		int ownedBusiness = Engine_UiRealEstatePage.ConditionKey(Estate(known: true, owned: true, hasTenant: false, typeMask: 0x020u));

		var keys = new HashSet<int>
		{
			unknown, unownedHome, unownedBusiness, ownedVacantHome, ownedTenantedHome, ownedBusiness,
		};
		AssertThat(keys.Count).IsEqual(6);
		AssertThat(ownedVacantHome).IsNotEqual(ownedBusiness);
	}

	/// <summary>
	/// End-to-end <see cref="Engine_UiScreenMenu"/> lifecycle: invalidates only when
	/// <see cref="Engine_UiRealEstatePage.ConditionKey"/> changes, updates the built rows, and
	/// carries cursor highlight across by row name.
	/// </summary>
	[TestCase, RequireGodotRuntime]
	public void ScreenMenuInvalidatesAndRebuildsAcrossEstateTransitions()
	{
		Fable2Bridge.RealEstate current = Estate(known: true, owned: true, hasTenant: false, typeMask: 1u);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateKnown, () => current.Known);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateOwned, () => current is { Known: true, Owned: true });
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateVacant, () => current is { Known: true, HasTenant: false });
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateTenanted, () => current is { Known: true, HasTenant: true });
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateBusiness, () => current.Known && current.IsBusiness);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateHome, () => current.Known && !current.IsBusiness);

		try
		{
			var model = new Engine_UiMenuModel();
			model.Apply(new Engine_UiModel.MenuChunk(
				EstatePresentation,
				"g_Menu:RepopulateMenuFromPopulationScript( 'RealEstateMenu' )",
				new Dictionary<string, string>(StringComparer.Ordinal)));

			var list = new Engine_UiScreenMenu
			{
				ScreenId = Engine_UiRealEstatePage.ScreenId,
				Model = model,
			};

			// First build on owned vacant home.
			AssertThat(list.InvalidateAuthoredIfChanged(Engine_UiRealEstatePage.ConditionKey(current))).IsTrue();
			AssertThat(list.Rebuild(EstatePresentation, screen: null)).IsTrue();
			AssertThat(string.Join(",", list.Script.Visible.Select(r => r.Name)))
				.IsEqual("GUI_SCREEN_REAL_ESTATE_SELL,GUI_SCREEN_REAL_ESTATE_RENT_OUT,GUI_SCREEN_REAL_ESTATE_SET_BUDGET");

			// Same state does not invalidate or rebuild.
			AssertThat(list.InvalidateAuthoredIfChanged(Engine_UiRealEstatePage.ConditionKey(current))).IsFalse();
			AssertThat(list.Rebuild(EstatePresentation, screen: null)).IsFalse();

			// Transition from owned vacant home -> owned business must invalidate, swap rows, and keep Sell highlighted.
			list.Script.Highlight(1);
			AssertThat(list.Script.Highlighted?.Name).IsEqual("GUI_SCREEN_REAL_ESTATE_SELL");

			current = Estate(known: true, owned: true, hasTenant: false, typeMask: 0x020u);
			AssertThat(list.InvalidateAuthoredIfChanged(Engine_UiRealEstatePage.ConditionKey(current))).IsTrue();
			AssertThat(list.Rebuild(EstatePresentation, screen: null)).IsTrue();
			AssertThat(string.Join(",", list.Script.Visible.Select(r => r.Name)))
				.IsEqual("GUI_SCREEN_REAL_ESTATE_SELL,GUI_SCREEN_REAL_ESTATE_ADJUST_PROFITS");
			AssertThat(list.Script.Highlighted?.Name).IsEqual("GUI_SCREEN_REAL_ESTATE_SELL");
		}
		finally
		{
			Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateKnown, () => false);
			Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateOwned, () => false);
			Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateVacant, () => false);
			Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateTenanted, () => false);
			Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateBusiness, () => false);
			Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateHome, () => false);
		}
	}
}
