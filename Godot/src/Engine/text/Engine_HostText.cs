using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Text;

/// <summary>Localizes common player-facing host text that has no entry in the game's Babel book.</summary>
public static class Engine_HostText
{
	private static readonly IReadOnlyDictionary<string, string> French =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["Multiplayer"] = "Multijoueur",
			["Quit to Desktop"] = "Quitter le jeu",
			["Quit Game"] = "Quitter le jeu",
			["Save and Quit to Title"] = "Sauvegarder et revenir au titre",
			["Save and Quit to Desktop"] = "Sauvegarder et quitter le jeu",
			["Save and Quit Game"] = "Sauvegarder et quitter le jeu",
			["Quit to Title and Don't Save"] = "Revenir au titre sans sauvegarder",
			["Quit to Desktop and Don't Save"] = "Quitter le jeu sans sauvegarder",
			["Quit Game and Don't Save"] = "Quitter le jeu sans sauvegarder",
			["Quit to Title"] = "Revenir à l'écran titre",
			["Achievements"] = "Succès",
			["Active Mods"] = "Mods actifs",
			["Mods"] = "Mods",
			["Leave or Quit"] = "Partir ou quitter",
			["Leave this co-op game (your Hero is saved)"] = "Quitter la coopération (votre héros sera sauvegardé)",
			["My game"] = "Ma partie",
			["Add a friend (paste code)"] = "Ajouter un ami (coller un code)",
			["My friend code"] = "Mon code ami",
			["Friends"] = "Amis",
			["Friend Requests"] = "Demandes d'amis",
			["Invitations"] = "Invitations",
			["Games"] = "Parties",
			["Player {0}"] = "Joueur {0}",
			["Back"] = "Retour",
			["Continue"] = "Continuer",
			["Cancel"] = "Annuler",
			["Accept"] = "Accepter",
			["Decline"] = "Refuser",
			["Yes"] = "Oui",
			["No"] = "Non",
			["On"] = "Activé",
			["Off"] = "Désactivé",
			["None"] = "Aucun",
			["Default"] = "Par défaut",
			["Reset"] = "Réinitialiser",
		};

	/// <summary>Resolves common host text using the game's current language.</summary>
	/// <param name="english">The authored English phrase.</param>
	/// <param name="arguments">Values inserted into any format placeholders.</param>
	public static string Get(string english, params object?[] arguments) =>
		ForCulture(Engine_GameCulture.Current, english, arguments);

	/// <summary>Resolves host text for a culture, then formats it with the supplied values.</summary>
	/// <param name="culture">The language code to resolve.</param>
	/// <param name="english">The authored English phrase.</param>
	/// <param name="arguments">Values inserted into any format placeholders.</param>
	public static string ForCulture(string culture, string english, params object?[] arguments)
	{
		bool french = culture.Equals("fr", StringComparison.OrdinalIgnoreCase) ||
			culture.StartsWith("fr-", StringComparison.OrdinalIgnoreCase);
		string text = french && French.TryGetValue(english, out string? translated) ? translated : english;
		return arguments.Length == 0 ? text : string.Format(
			french ? CultureInfo.GetCultureInfo("fr-FR") : CultureInfo.InvariantCulture, text, arguments);
	}
}
