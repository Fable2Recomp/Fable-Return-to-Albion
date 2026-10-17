-- Souls Combat: enemy pacing.
--
-- Runs once per game session, after the shipped scripts have defined their tables. Everything
-- here patches those tables; nothing replaces a shipped file.
--
-- What the shipped AI does (ai/combatwheel.lua, ai/combatinfo.lua):
--   * a defender's CombatWheel lets up to 3 attackers (the inner ring's ProngLimit) hold the
--     attack token at once, and takes a token back after ActiveAttackerTimeOut = 3 seconds;
--   * an attacker swings again once MinSecondsBetweenMeleeAttacks of its style has passed,
--     read live from the style table on every update;
--   * a style with NonFormation = true never queues for a token at all (hollow men, beetles,
--     trolls, banshees, the dog, Lucien's soldiers). CombatInfo copies the flag when it is
--     created, which is after this file runs.

SoulsCombat = SoulsCombat or {}
local S = SoulsCombat

-- The knobs. Tuned in a run, not here.
S.AttackersAtOnce = 2         -- shipped 3
S.TokenSeconds = 5            -- shipped 3
S.MinSecondsBetweenSwings = 1.6
S.QueueEveryone = true        -- clear NonFormation so every style takes turns

if CombatWheel then
	CombatWheel.ActiveAttackerTimeOut = S.TokenSeconds

	-- ProngLimit is a literal in CombatWheel:Create, captured per wheel, so the constructor is
	-- wrapped: every wheel made from here on gets the new limit.
	local create = CombatWheel.Create
	if create then
		CombatWheel.Create = function(self, defender_info)
			local wheel = create(self, defender_info)
			if wheel and wheel.InnerRing then
				wheel.InnerRing.ProngLimit = S.AttackersAtOnce
			end
			return wheel
		end
	end
end

if CombatStyles then
	for _, style in pairs(CombatStyles) do
		if type(style) == "table" then
			local gap = style.MinSecondsBetweenMeleeAttacks or 0
			if gap < S.MinSecondsBetweenSwings then
				style.MinSecondsBetweenMeleeAttacks = S.MinSecondsBetweenSwings
			end
			if S.QueueEveryone then
				style.NonFormation = nil
			end
		end
	end
end

-- The hero's combat balance (combatbalance.lua defines the table; the player copies it when it
-- is created, after this runs — sub_823E9BF0 — and the parry action reads the copy by name,
-- sub_82986B28 -> sub_829897B0). The timed parry: a quarter-second window instead of a full
-- second, in a 90-degree wedge.
if HeroCombatBalanceParams then
	HeroCombatBalanceParams.InputWindowSecondsForParry = 0.25
	HeroCombatBalanceParams.MeleeParryWedgeAngle = 90
end
