using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Refii.Ui;

/// <summary>
/// THE WORLD MAP'S QUEST AND JOB PAGES, built from the quest object's own fields.
///
/// The quest page is not a set of labelled rows: `sub_829AF638` writes ONE concatenated
/// rich-text string into `QuestsPage.Contents.Description` and everything the page says lives
/// inside it — the banner, the rewards, the current task, the completed tasks, and only then the
/// description itself. The job page is the opposite shape, a title and six separate elements.
///
/// Both were blank here because none of it crossed: the quest export carried a record id and
/// five flags, and the page's numbers — `Rating`, `RewardMoney`, `RewardRenown`, the objective's
/// `%1` parameter, `DaysRemaining` — are plain fields on the quest object that nothing read.
/// Task lines are a vector at `quest+104`/`+108`, stride 8, each `{ Babel tag, Succeeded }`, and
/// POSITION is what makes one current rather than completed: the last entry is the current task
/// and everything before it is done. The `Succeeded` byte is serialised and never read by the
/// page — reproducing it would be inventing a rule the guest does not have.
/// </summary>
public static class Engine_UiQuestPages
{
	// The quest page — one title and one blob.
	private const string QuestTitle = "QuestsPage.Contents.PageTitle";

	private const string QuestBody = "QuestsPage.Contents.Description";

	// The job page — a title, a rating, and six lines.
	private const string JobTitle = "JobsPage.Contents.PageTitle";

	private const string JobDescription = "JobsPage.Contents.Description";

	private const string JobObjective = "JobsPage.Contents.Objective";

	private const string JobTime = "JobsPage.Contents.Lines.Time";

	private const string JobRegion = "JobsPage.Contents.Lines.RegionName";

	private const string JobDistance = "JobsPage.Contents.Lines.DistanceAmount";

	private const string JobTravel = "JobsPage.Contents.Lines.TravelAmount";

	/// <summary>The A-button graphic, which `sub_82426328` shows or hides outright.</summary>
	public const string JobAButton = "JobsPage.Contents.AButton";

	/// <summary>Its caption — one of three tags, picked by the pump from the guest's own tests.</summary>
	private const string JobAButtonText = "JobsPage.Contents.AButton.ButtonText";

	// The sales page — a title and one blob, like the quest page.
	private const string SaleTitle = "SalesPage.Contents.PageTitle";

	private const string SaleBody = "SalesPage.Contents.Description";

	private const string DaysLeftTag = "GUI_SCREEN_WORLD_MAP_SALES_NUMBER_OF_DAYS_LEFT";

	private const string PriceUpTag = "GUI_SCREEN_WORLD_MAP_SALES_PAGE_PRICE_INCREASE";

	private const string PriceDownTag = "GUI_SCREEN_WORLD_MAP_SALES_PAGE_PRICE_DECREASE";

	/// <summary>The star strip's slide — the quest's own `Rating`, written unclamped.</summary>
	public const string JobStarRating = "JobsPage.Contents.StarRating";

	/// <summary>The second strip, which the guest sets only for a rating of 1..5.</summary>
	public const string JobStars = "JobsPage.Contents.Stars";

	// The rich-text vocabulary, all wide literals in the shipped page.
	private const string Break = "<br>";

	private const string HeadOpen = "<s1>";

	private const string HeadClose = "</s1>";

	private const string Gold = "<gold_img>";

	private const string Bullet = "<bullet.img>";

	/// <summary>The token every task line substitutes with `CurrentObjectiveParameter`.</summary>
	private const string Parameter = "%1";

	private const string PrimaryTag = "GUI_SCREEN_WORLD_MAP_QUESTS_THIS_IS_YOUR_PRIMARY_QUEST";
	private const string ActiveTag = "GUI_SCREEN_WORLD_MAP_QUESTS_THIS_QUEST_IS_ACTIVE";
	private const string RewardsTag = "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_REWARDS";
	private const string MoneyTag = "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_MONEY";
	private const string RenownTag = "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_RENOWN";
	private const string CurrentTaskTag = "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_CURRENT_TASK";
	private const string CompletedTasksTag = "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_COMPLETED_TASKS";
	private const string DescriptionTag = "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_DESCRIPTION";

	/// <summary>Every key these two pages own, blanked — the page states its own absence.</summary>
	public static void Blank(Dictionary<string, string> values)
	{
		values[QuestTitle] = string.Empty;
		values[QuestBody] = string.Empty;
		values[JobTitle] = string.Empty;
		values[JobDescription] = string.Empty;
		values[JobObjective] = string.Empty;
		values[JobTime] = string.Empty;
		values[JobRegion] = string.Empty;
		values[JobDistance] = string.Empty;
		values[JobTravel] = string.Empty;
		values[SaleTitle] = string.Empty;
		values[SaleBody] = string.Empty;
	}

	/// <summary>
	/// THE SALES PAGE, which was blank down to its title. `sub_829AAB50` writes a title resolved
	/// by Babel KEY HASH — the sale hashed its tags when it activated, so no tag survives to
	/// look up by name — and one concatenated body: description, region, the days left, and the
	/// price change as a SIGNED percentage of `(multiplier - 1) * 100`. A sale is below 1.0 and
	/// reads negative, which is the guest's own arithmetic and not a bug to correct.
	/// </summary>
	public static void FillSale(
		Dictionary<string, string> values, Refii.Host.HostBridge.Sale sale,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		values[SaleTitle] = Key(strings, sale.NameKey);

		var body = new StringBuilder();
		body.Append(Key(strings, sale.DescriptionKey)).Append(Break).Append(Break);
		body.Append(Key(strings, sale.RegionKey)).Append(Break).Append(Break);

		body.Append(Babel(strings, DaysLeftTag)).Append(" <s1>")
			.Append(sale.DaysLeft.ToString(CultureInfo.InvariantCulture))
			.Append(" </s1>").Append(Break).Append(Break);

		float change = (sale.Multiplier - 1f) * 100f;
		body.Append(Babel(strings, sale.IsShortage ? PriceUpTag : PriceDownTag))
			.Append(" <s1>").Append(change.ToString("0.#", CultureInfo.InvariantCulture))
			.Append("%</s1>").Append(Break).Append(Break);

		values[SaleBody] = body.ToString();
	}

	/// <summary>
	/// Whether the job page's A-button stands for this quest. The pump answers a tick late, so
	/// a journey for a different row says nothing about this one and the button stays down.
	/// </summary>
	public static bool JobPromptShown(uint definition)
	{
		Refii.Host.HostBridge.QuestJourney journey = Refii.Host.HostBridge.GetQuestTravel();
		return journey.IsFor(definition) && journey.Prompt.Length > 0;
	}

	/// <summary>A Babel string by KEY HASH rather than by tag name.</summary>
	private static string Key(Refii.Text.Engine_BabelDatabase? strings, uint key) =>
		strings is { } book && book.TryGetString(key, out string resolved) ? resolved : string.Empty;

	/// <summary>
	/// Fills whichever of the two pages this row belongs to. `definition` is the quest's GDB
	/// record hash, which is both the row's id and the quest object's `RecordID`.
	/// </summary>
	public static void Fill(
		Dictionary<string, string> values, uint definition, bool job,
		string title, Refii.Text.Engine_BabelDatabase? strings)
	{
		Refii.Host.HostBridge.QuestEntry? found = null;
		foreach (Refii.Host.HostBridge.QuestEntry entry in Refii.Host.HostBridge.GetQuestLog())
		{
			if (entry.RecordId == definition)
			{
				found = entry;
				break;
			}
		}

		if (found is not { } quest)
		{
			// The title still stands: the row resolved it from GDB even when the live quest is
			// not in the log, which is a real state during a level change.
			values[job ? JobTitle : QuestTitle] = title;
			return;
		}

		// Name the journey BEFORE reading it: the pump samples on its own thread and answers a
		// tick later, so asking now makes the next frame's answer this row's.
		if (job)
		{
			Refii.Host.HostBridge.SetQuestTravel(definition);
		}

		List<string> tasks = Refii.Host.HostBridge.GetQuestTasks(definition);
		string description = Engine_UiQuestSource.DescriptionOf(definition);

		if (job)
		{
			FillJob(values, quest, tasks, title, description, strings);
			return;
		}

		values[QuestTitle] = title;
		values[QuestBody] = QuestBodyText(quest, tasks, description, strings);
	}

	/// <summary>
	/// The quest page's single string, in the guest's own emission order (`sub_829AF7F8`).
	/// A quest that is neither primary nor active opens with no banner at all — the shipped
	/// function falls straight through.
	/// </summary>
	private static string QuestBodyText(
		Refii.Host.HostBridge.QuestEntry quest, IReadOnlyList<string> tasks, string description,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var body = new StringBuilder();

		if (quest.Primary)
		{
			body.Append(Babel(strings, PrimaryTag)).Append(Break).Append(Break);
		}
		else if (quest.Active)
		{
			body.Append(Babel(strings, ActiveTag)).Append(Break).Append(Break);
		}

		// REWARDS — the block appears only when there is one, and each line only when its own
		// number is above zero.
		if (quest.RewardMoney > 0 || quest.RewardRenown > 0)
		{
			Heading(body, Babel(strings, RewardsTag));

			if (quest.RewardMoney > 0)
			{
				body.Append(Gold).Append(' ').Append(Babel(strings, MoneyTag)).Append(' ')
					.Append(quest.RewardMoney.ToString(CultureInfo.InvariantCulture))
					.Append(Break).Append(Break);
			}

			if (quest.RewardRenown > 0)
			{
				body.Append(Bullet).Append(' ').Append(Babel(strings, RenownTag)).Append(' ')
					.Append(quest.RewardRenown.ToString(CultureInfo.InvariantCulture))
					.Append(Break).Append(Break);
			}
		}

		// THE CURRENT TASK IS THE LAST ENTRY, and the completed ones are everything before it.
		if (tasks.Count > 0)
		{
			Heading(body, Babel(strings, CurrentTaskTag));
			body.Append(Task(strings, tasks[^1], quest.ObjectiveParameter))
				.Append(Break).Append(Break);
		}

		if (tasks.Count > 1)
		{
			Heading(body, Babel(strings, CompletedTasksTag));
			for (int at = 0; at < tasks.Count - 1; ++at)
			{
				body.Append(Bullet).Append(' ')
					.Append(Task(strings, tasks[at], quest.ObjectiveParameter))
					.Append(Break);
			}

			body.Append(Break);
		}

		Heading(body, Babel(strings, DescriptionTag));
		body.Append(description).Append(Break).Append(Break);
		return body.ToString();
	}

	/// <summary>
	/// The job page's separate lines. Distance and travel come from the region sample rather
	/// than from a second travel-graph read — the same two numbers the region page shows.
	/// </summary>
	private static void FillJob(
		Dictionary<string, string> values, Refii.Host.HostBridge.QuestEntry quest,
		IReadOnlyList<string> tasks, string title, string description,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		values[JobTitle] = title;
		values[JobDescription] = description;

		// The objective is the LAST task line, with `%1` substituted — the same rule the quest
		// page's "current task" uses, because it is the same call (`sub_8230E268`).
		values[JobObjective] = tasks.Count > 0
			? Task(strings, tasks[^1], quest.ObjectiveParameter)
			: string.Empty;

		// THE JOURNEY IS THE PUMP'S ANSWER, a tick behind the ask — so it is drawn only when it
		// is for THIS quest. The two providers allocate and must run on the guest's own thread;
		// the region page's numbers would be the wrong journey entirely.
		Refii.Host.HostBridge.QuestJourney journey = Refii.Host.HostBridge.GetQuestTravel();
		if (journey.IsFor(quest.RecordId))
		{
			values[JobDistance] = journey.Distance;
			values[JobTravel] = journey.Travel;

			// TIME REMAINING IS THE GUEST'S SENTENCE, not ours to assemble.
			// `GUI_SHARED_X_DAYS` is a PRINTF format string that `sub_82B3F250` fills, not a
			// `%1` template — building it here found no token and left the format itself on the
			// page. `sub_823C9228` makes the singular/plural choice too, so both go with it.
			values[JobTime] = journey.Days;

			// The region crosses as a TAG — `sub_829AFE58` sets it with `sub_82425F40`, the
			// set-by-tag setter, where the distance and travel lines beside it take an already
			// formatted LHString through `sub_82425DD0`. A level no region claims resolves to
			// nothing, and the line is then blanked rather than left on its placeholder.
			values[JobRegion] = journey.Region.Length == 0
				? string.Empty
				: Babel(strings, journey.Region);

			// An empty prompt is the guest HIDING the button, not a failed read — two of
			// `sub_824708D8`'s branches return without setting any text at all.
			values[JobAButtonText] = journey.Prompt.Length == 0
				? string.Empty
				: Babel(strings, journey.Prompt);
		}
	}

	/// <summary>`&lt;s1&gt;heading&lt;/s1&gt;&lt;br&gt;` — the shipped block header.</summary>
	private static void Heading(StringBuilder body, string heading) =>
		body.Append(HeadOpen).Append(heading).Append(HeadClose).Append(Break);

	/// <summary>
	/// One task line: the Babel tag resolved, with `%1` replaced by the quest's
	/// `CurrentObjectiveParameter`. `sub_8230E268` substitutes only when the token is present.
	/// </summary>
	private static string Task(
		Refii.Text.Engine_BabelDatabase? strings, string tag, int parameter)
	{
		string line = Babel(strings, tag);
		return line.Contains(Parameter, System.StringComparison.Ordinal)
			? line.Replace(Parameter, parameter.ToString(CultureInfo.InvariantCulture),
				System.StringComparison.Ordinal)
			: line;
	}


	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is { } book && book.TryGetString(tag, out string resolved) ? resolved : string.Empty;
}
