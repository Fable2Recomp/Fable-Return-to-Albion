-- Souls Combat: the enemies' fighting.
--
-- A module of init.lua (require("ai")).

local S = SoulsCombat
local C = require("common")
local Setting = C.Setting

----------------------------------------------------------------------------------------------------
-- ENEMIES (scripts/ai/combatwheel.lua, combatwheelring.lua, combatinfo.lua, combatsequences.lua,
-- combatstates/primarycombatstates.lua, combatstyles/*.lua)
--
-- How the shipped AI takes turns: each defender has a CombatWheel whose inner ring holds
-- ProngLimit slots round the target (`t.InnerRing = WheelRing:Create({ProngLimit = 3 })`,
-- CombatWheel:Create); everyone else waits on the outer ring. The wheel grants ONE attack token
-- (CombatWheel:SetActiveAttacker: `arg1.IsActiveAttacker = true ... self.ActiveAttacker = arg1`);
-- an enemy swings only while it holds it (CombatInfo:CanAttackTarget: `arg1 or self.ReadyToAttack
-- and self.IsActiveAttacker or Combat.IsEnraged(self.Combatant)`), gives it back after its sequence
-- (PlayCombatSequence: `self.CombatInfo.IsActiveAttacker = false`), and loses it after
-- ActiveAttackerTimeOut (`CombatWheel = {ActiveAttackerTimeOut = 3 }`) without attacking. On top
-- of that, nobody may swing for SecondsToWaitAfterTargetIsHit after ANYONE hit the target, nor
-- within MinSecondsBetweenMeleeAttacks of its own last sequence (CombatInfo:IsValidTimeToAttack).
-- NonFormation styles skip the wheel (CombatInfo:Update: `if self.NonFormation then
-- self.IsActiveAttacker = true`) — the game's own aggro mode, kept.
--
-- The souls pack: two tokens and four slots per defender, a short token, swing gaps by tier,
-- combos that commit, enemies that punish a heal or a whiffed swing, blocks that can come on any
-- attack and turn into a riposte, circling instead of standing, and boss enrage phases.
--
-- WHEN: the game VM runs the AI scripts (scripts/ai/aisetupscript.lua's AddAIScriptFile list) with
-- its other startup scripts, and a mod's init.lua after them; each rule still tests its globals.
----------------------------------------------------------------------------------------------------

-- EVERY SETTING IS READ AT USE: the numbers below are read where they act (each wheel update,
-- each combat update), so a live tune reaches the next one; the style patches that the game reads
-- by name every update are re-applied when a gap setting changes (setting_changed).
--
-- ONE CLOCK: every time this file keeps of its own is Refii.now(); the shipped AI's own stamps
-- (CombatInfo.LastAttackTime, world frames) stay on the game's Timing, which wrote them.

-- A combat style, not one of the other tables CombatStyles also holds (StandardMeleeCombatStates,
-- ShootingBalanceDataDefault, GetStyleForCreature ...).
local function IsStyle(style)
	return type(style) == "table" and style.ValidStates ~= nil and style.Limits ~= nil
end

-- Casting is an opening. Each hero's last spell launch (the spell_launched event, after the
-- spell really exists), by handle.
S.CastExtraTokens = 1
S.Cast = {}

Refii.on("spell_launched", function(e)
	if e.player ~= 0 then
		S.Cast[e.caster] = Refii.now()
	end
end)

function S.RecentlyCast(entity)
	if entity == nil then
		return false
	end
	local at = S.Cast[Refii.handle(entity)]
	return at ~= nil and Refii.now() - at < Setting("ai_punish_cast_seconds", 1.5)
end


-- Tiers: a leader, a style with a boss health bar or a troll is a boss, from the styles' own
-- data (IsLeaderType, UsesHealthBar, IsTroll); a creature whose definition states it elite or a
-- lieutenant (Refii.creature_tier: CREATURE_ELITE, CREATURE_LIEUTENANT) is elite; the rest is
-- fodder. Each tier caps the swing gap and the courtesy waits, which the shipped AI reads off the
-- STYLE by name on every update (CombatInfo:IsValidTimeToAttack, IsAllowedToAttack): a style
-- starts as boss or fodder, and becomes elite on the first update of an elite creature fighting
-- with it. A style that ships quicker keeps its own number. The block chance is the combatant's
-- own tier.
S.Tiers = {
	fodder = { rank = 1, gap_key = "ai_gap_fodder", gap_default = 2.0, hit = 1.0, attacked = 0.75, block_key = "ai_block_chance_fodder", block_default = 15 },
	elite = { rank = 2, gap_key = "ai_gap_elite", gap_default = 1.2, hit = 0.6, attacked = 0.4, block_key = "ai_block_chance_elite", block_default = 35 },
	boss = { rank = 3, gap_key = "ai_gap_boss", gap_default = 0.7, hit = 0.3, attacked = 0, block_key = "ai_block_chance_boss", block_default = 50 },
}

-- A tier's swing gap and block chance, read live by the tier's own setting key.
local function TierGap(tier)
	return Setting(S.Tiers[tier].gap_key, S.Tiers[tier].gap_default)
end

local function TierBlock(tier)
	return Setting(S.Tiers[tier].block_key, S.Tiers[tier].block_default)
end

local function StyleTier(style)
	if style.IsLeaderType or style.UsesHealthBar or style.IsTroll then
		return "boss"
	end
	return "fodder"
end

local function HasBit(bits, bit)
	return math.floor(bits / bit) % 2 == 1
end

-- A combatant's tier: its style's boss data, else the rank its creature definition states.
local function CombatantTier(info)
	if StyleTier(info.Style) == "boss" then
		return "boss"
	end
	local bits = info.Combatant and Refii.creature_tier(Refii.handle(info.Combatant))
	if bits ~= nil and (HasBit(bits, Refii.CREATURE_ELITE) or HasBit(bits, Refii.CREATURE_LIEUTENANT)) then
		return "elite"
	end
	return "fodder"
end

-- The caps on one style's own numbers. The shipped number is kept on the style the first time
-- (SoulsShippedGap), so a wider gap after a narrower one still computes from the shipped base; the
-- courtesy waits are fixed per tier. A formation break's SecondsToWaitAfterTargetIsHitOverride
-- (CombatGroupMind:BreakFormation, 0.5) still wins over the style's wait, as shipped.
local function ApplyStyleCaps(style)
	local tier = S.Tiers[style.SoulsTier]
	if style.MinSecondsBetweenMeleeAttacks ~= nil then
		style.SoulsShippedGap = style.SoulsShippedGap or style.MinSecondsBetweenMeleeAttacks
		style.MinSecondsBetweenMeleeAttacks = math.min(style.SoulsShippedGap, TierGap(style.SoulsTier))
	end
	if style.SecondsToWaitAfterTargetIsHit then
		style.SecondsToWaitAfterTargetIsHit = math.min(style.SecondsToWaitAfterTargetIsHit, tier.hit)
	end
	if style.SecondsToWaitAfterTargetIsAttacked then
		style.SecondsToWaitAfterTargetIsAttacked = math.min(style.SecondsToWaitAfterTargetIsAttacked, tier.attacked)
	end
end

local function ApplyTierCaps()
	if not CombatStyles then
		return
	end
	for _, style in pairs(CombatStyles) do
		if IsStyle(style) then
			style.SoulsTier = style.SoulsTier or StyleTier(style)
			ApplyStyleCaps(style)
		end
	end
end

-- Each combatant's tier, on its first update with a style (CombatInfo.Update below); a style
-- fought with by a higher tier than it holds is raised to it.
function S.NoteCombatantTier(info)
	info.SoulsTierStyle = info.Style
	info.SoulsBlockChance = nil
	if not IsStyle(info.Style) then
		return
	end
	info.SoulsTier = CombatantTier(info)
	local style = info.Style
	style.SoulsTier = style.SoulsTier or StyleTier(style)
	if S.Tiers[info.SoulsTier].rank > S.Tiers[style.SoulsTier].rank then
		style.SoulsTier = info.SoulsTier
		ApplyStyleCaps(style)
	end
end

local kGapSettings = {
	["souls_combat.ai_gap_fodder"] = true,
	["souls_combat.ai_gap_elite"] = true,
	["souls_combat.ai_gap_boss"] = true,
}

ApplyTierCaps()
Refii.on("setting_changed", function(e)
	if kGapSettings[e.key] then
		ApplyTierCaps()
	end
end)

-- Blocks: Fable III's enemies block in the engine, not in a combat state. CombatInfo:ApplyStyle
-- hands the style's BlockData to the creature's block AI (`Block.SetAIBlockingBalanceData(
-- self.Combatant, self.Style.BlockData)`), whose reader (TU3 sub_82A3C108 / disc sub_829FBF88)
-- copies PercentChanceToBlockInitialHit to the block AI +0x84 (default 0.0). The block AI's attack
-- check (CECBlockAI_vf24, TU3 0x82A3B1F8 / disc 0x829FB078), for an attack due within its soft-block
-- window and while no guard is up, rolls that percent and raises the style's PrimaryBlockType
-- (TU3 `lfs f0,0x84(r26)` 0x82a3b5a8, `lfs f13,0x84(r26)` 0x82a3b5f0, `lwz r4,0xA4(r26)` 0x82a3b61c;
-- disc 0x829fb428 / 0x829fb470 / 0x829fb49c); the block then lowers on its own SecondsToBlockFor.
-- Every shipped style but three (50, 50, 100) leaves the percent at 0 and blocks only after
-- NumberOfHitsToForcePrimaryBlock hits. Each blocking combatant is now handed a copy of its style's
-- BlockData carrying its tier's chance (the shipped one when higher), through the same call, again
-- when the setting or its style changes.
local function PushBlockChance(info)
	local style = info.Style
	if not (style.CanBlock and style.BlockData and info.SoulsTier) then
		return
	end
	local chance = TierBlock(info.SoulsTier)
	local shipped = style.BlockData.PercentChanceToBlockInitialHit or 0
	if chance < shipped then
		chance = shipped
	end
	if info.SoulsBlockChance == chance or not (Block and Block.IsAvailable(info.Combatant)) then
		return
	end
	local data = {}
	for k, v in pairs(style.BlockData) do
		data[k] = v
	end
	data.PercentChanceToBlockInitialHit = chance
	Block.SetAIBlockingBalanceData(info.Combatant, data)
	info.SoulsBlockChance = chance
end


-- Punish windows: right after a hero heals, or ends a swing that hit nothing, up to two enemies in
-- reach look for one of their own attack sequences as if they held the token. Keyed by player
-- (Refii.player_of).
--   health_changed — a hero's health rose (delta > 0) through the game's own Modify: a potion,
--     food, a heal over time. The health component's own regeneration is not a heal: its update
--     calls Modify with the owner as the source (TU3 `lwz r5,4(r28)` 0x82629930, `bl` 0x82629960),
--     where a heal over time passes none (`li r5,0` 0x826296f8). A heal while its window is open
--     keeps that window, so a heal over time is one opening per window, not one per tick.
--   melee_swing_ended — the hero's strike ended with `hits` 0: a whiff. The window opens when the
--     strike ends and lasts ai_punish_whiff_seconds.
S.Punish = {
	per_window = 2,
	own_gap = 0.8,
}
S.Healed = {}
S.Whiffed = {}
S.Claims = {}

local function HeroActed(player, times)
	times[player] = Refii.now()
	S.Claims = {}
end

Refii.on("health_changed", function(e)
	if (e.player == 1 or e.player == 2) and e.delta > 0 and e.source ~= e.entity then
		local healed = S.Healed[e.player]
		if healed == nil or Refii.now() - healed >= Setting("ai_punish_heal_seconds", 1.6) then
			HeroActed(e.player, S.Healed)
		end
	end
end)

Refii.on("melee_swing_ended", function(e)
	if (e.player == 1 or e.player == 2) and e.hits == 0 then
		HeroActed(e.player, S.Whiffed)
	end
end)

local function PunishWindow(info)
	local target = info.Target
	if target == nil then
		return nil
	end
	if info.Zone ~= CombatZones.Near and info.Zone ~= CombatZones.Middle then
		return nil
	end
	if info.LastAttackTime and Timing.GetSecondsSince(info.LastAttackTime) < S.Punish.own_gap then
		return nil
	end
	local player = Refii.player_of(Refii.handle(target))
	if player ~= 1 and player ~= 2 then
		return nil
	end
	local now = Refii.now()
	local healed = S.Healed[player]
	if healed and now - healed < Setting("ai_punish_heal_seconds", 1.6) then
		return "healed" .. player .. ":" .. healed
	end
	local whiffed = S.Whiffed[player]
	if whiffed and now - whiffed < Setting("ai_punish_whiff_seconds", 1.1) then
		return "whiffed" .. player .. ":" .. whiffed
	end
	return nil
end

-- EACH RUN OF THE GAME'S AI SCRIPTS. Fable III runs combatwheel.lua, combatinfo.lua, the styles and
-- the sequences at each main-game world init (a new game, every save load:
-- lhLuaScriptSystem_RunPostLoadScripts, after this file runs), and each run builds new tables. The
-- runtime raises lua_ready after them, so the patches below go on the tables of the run they find,
-- once per run; with no AI tables yet (the front end, before the first world) nothing happens.
function S.ApplyAI()
	if CombatInfo == nil or (S.AppliedInfo == CombatInfo and S.AppliedStyles == CombatStyles) then
		return
	end
	S.AppliedInfo, S.AppliedStyles = CombatInfo, CombatStyles
	-- The shipped functions the wraps call are this run's.
	S.WheelCreate, S.UpdateActiveAttacker, S.ApplyStyle, S.CombatInfoUpdate, S.FindBestSequence = nil, nil, nil, nil, nil
	-- The styles' gaps by tier: the styles are this run's too.
	ApplyTierCaps()

	if CombatWheel and WheelRing then
		CombatWheel.ActiveAttackerTimeOut = Setting("ai_token_seconds", 1.5)

		-- ProngLimit is a literal in CombatWheel:Create, kept on each wheel's ring; WheelRing:Add and
		-- WheelRing:GrabBestAttackers (`self.ProngLimit or #arg3 + 1`, at each Rejig) read it. Restated
		-- on every update with the timeout, so a live change reaches the wheels that exist.
		S.WheelCreate = S.WheelCreate or CombatWheel.Create
		CombatWheel.Create = function(self, defender_info)
			local wheel = S.WheelCreate(self, defender_info)
			if wheel and wheel.InnerRing then
				wheel.InnerRing.ProngLimit = Setting("ai_ring_slots", 4)
			end
			return wheel
		end

		-- The second (and further) tokens: after the wheel's own grant, inner-ring attackers that are
		-- ready get a token too, longest-idle first (WheelRing:FindBestAttacker's order: no
		-- LastAttackTime first, then the earliest), up to the limit. They give it back exactly as the
		-- wheel's own holder does: after their sequence (PlayCombatSequence clears IsActiveAttacker) or
		-- after the timeout without attacking (CombatWheel:UpdateActiveAttacker's
		-- `not Action.IsAttackingViaAction(...)` test).
		local function Earlier(a, b)
			if a == nil then
				return b
			end
			if a.LastAttackTime == nil then
				return a
			end
			if b.LastAttackTime == nil or b.LastAttackTime < a.LastAttackTime then
				return b
			end
			return a
		end

		local function GrantExtraTokens(wheel)
			local held = wheel.SoulsTokens
			if held == nil then
				held = {}
				wheel.SoulsTokens = held
			end
			local count = (wheel.ActiveAttacker and wheel.ActiveAttacker.IsActiveAttacker) and 1 or 0
			for info, tick in pairs(held) do
				if info == wheel.ActiveAttacker or not IsCombatantAlive(info) or not info.IsActiveAttacker then
					held[info] = nil
				elseif Refii.now() - tick >= wheel.ActiveAttackerTimeOut and not Action.IsAttackingViaAction(info.Combatant) then
					info.IsActiveAttacker = false
					held[info] = nil
				else
					count = count + 1
				end
			end
			local prongs = wheel.InnerRing and wheel.InnerRing.Prongs
			if prongs == nil then
				return
			end
			-- A defender who just launched a spell has opened up: one more token, and it goes to an
			-- enemy not yet ready too; a holder closes in (the closing-in rule below).
			local limit = Setting("ai_attackers_at_once", 2)
			local casting = S.RecentlyCast(wheel.Defender)
			if casting then
				limit = limit + S.CastExtraTokens
			end
			while count < limit do
				local pick
				for _, prong in ipairs(prongs) do
					local info = prong.Attacker
					if IsCombatantAlive(info) and (info.ReadyToAttack or casting) and not info.IsActiveAttacker then
						pick = Earlier(pick, info)
					end
				end
				if pick == nil then
					return
				end
				pick.IsActiveAttacker = true
				held[pick] = Refii.now()
				count = count + 1
			end
		end

		S.UpdateActiveAttacker = S.UpdateActiveAttacker or CombatWheel.UpdateActiveAttacker
		CombatWheel.UpdateActiveAttacker = function(self)
			CombatWheel.ActiveAttackerTimeOut = Setting("ai_token_seconds", 1.5)
			if self.InnerRing then
				self.InnerRing.ProngLimit = Setting("ai_ring_slots", 4)
			end
			local granted = S.UpdateActiveAttacker(self)
			GrantExtraTokens(self)
			return granted
		end
	end

	-- ApplyStyle hands the style's own BlockData to the block AI again: the tier's copy follows it.
	if CombatInfo and CombatInfo.ApplyStyle then
		S.ApplyStyle = S.ApplyStyle or CombatInfo.ApplyStyle
		CombatInfo.ApplyStyle = function(self, ...)
			local result = S.ApplyStyle(self, ...)
			self.SoulsBlockChance = nil
			return result
		end
	end

	-- Closing in: within EnterStrafeDist of its target (the style's, else 5, copied by ApplyStyle) an
	-- enemy is ReadyToStrafe (CombatInfo:Update: `if enter_strafe_dist > self.DistToTarget then
	-- self.ReadyToStrafe = true`), BehaviourCombat:Update then starts strafing (AIMovement.SetStrafe),
	-- and BehaviourCombat:MoveTo walks a strafing creature (`EM_BIPED_STRAFE` -> NAV_SPEED_WALK) where
	-- it would sprint. A token holder now sprints to within ai_close_in_distance before it strafes;
	-- without a token the style's own distance stands.
	if CombatInfo and CombatInfo.Update then
		S.CombatInfoUpdate = S.CombatInfoUpdate or CombatInfo.Update
		CombatInfo.Update = function(self, ...)
			if self.SoulsTierStyle ~= self.Style then
				S.NoteCombatantTier(self)
			end
			if self.SoulsTier and IsStyle(self.Style) then
				PushBlockChance(self)
			end
			if self.EnterStrafeDist ~= nil then
				if self.SoulsStrafeDist == nil or self.SoulsStrafeDistFrom ~= self.Style then
					self.SoulsStrafeDist = self.EnterStrafeDist
					self.SoulsStrafeDistFrom = self.Style
				end
				if self.IsActiveAttacker then
					self.EnterStrafeDist = math.min(self.SoulsStrafeDist, Setting("ai_close_in_distance", 3))
				else
					self.EnterStrafeDist = self.SoulsStrafeDist
				end
			end
			return S.CombatInfoUpdate(self, ...)
		end
	end

	-- The shipped anti-spam rule (CombatStates:AntiSpamCheck, in CombatStyles.StandardMeleeCombatStates
	-- and StandardRangedCombatStates): when SpellsToTrigger spells (or ShotsToTrigger shots) hit an
	-- enemy within TimeToCheckFor seconds (Combat.GetNumberOfHitsOfTypeInTime), its group breaks
	-- formation (CombatGroupMind:BreakFormation(TimeToAggroFor)). Shipped on some styles only; now at
	-- most 2 spells or 3 shots in at least 6 s, for at least 8 s, on every style in a group.
	if CombatStyles then
		for _, style in pairs(CombatStyles) do
			if IsStyle(style) then
				local spam = style.AntiSpamData
				if spam == nil then
					spam = { TimeToCheckFor = 6, ShotsToTrigger = 3, SpellsToTrigger = 2, TimeToAggroFor = 8 }
					style.AntiSpamData = spam
				else
					spam.TimeToCheckFor = math.max(spam.TimeToCheckFor or 0, 6)
					spam.ShotsToTrigger = math.min(spam.ShotsToTrigger or 3, 3)
					spam.SpellsToTrigger = math.min(spam.SpellsToTrigger or 2, 2)
					spam.TimeToAggroFor = math.max(spam.TimeToAggroFor or 0, 8)
				end
			end
		end
	end

	-- Combos commit: an interruptible sequence is broken off (Action.AttemptComboBreak) whenever the
	-- target leaves its ZonesWhilePlaying or a better sequence appears (PlayCombatSequence: `local v5 =
	-- not ...Uninterruptable and not ...CounterAttack`); an Uninterruptable one plays out, so a roll
	-- away leaves the enemy finishing its string — the opening.
	if CombatSequences and SequencePriorities then
		for _, sequence in pairs(CombatSequences) do
			if type(sequence) == "table" and sequence.Actions and sequence.IsCombo and not sequence.CounterAttack
				and sequence.Priority and sequence.Priority >= SequencePriorities.Attack then
				sequence.Uninterruptable = true
			end
		end
	end

	-- CombatSequences:FindBestSequence(info, can_attack): with can_attack (or a blocking situation)
	-- attack priorities score (`if arg2 or v5 then movement = SequencePriorities.PreferredAttack`).
	if CombatSequences and CombatSequences.FindBestSequence and CombatZones and SequencePriorities then
		S.FindBestSequence = S.FindBestSequence or CombatSequences.FindBestSequence
		CombatSequences.FindBestSequence = function(self, info, is_attack)
			if not is_attack and info.ReadyForSequence then
				local window = PunishWindow(info)
				if window then
					local claim = S.Claims[window]
					if claim == nil then
						claim = { count = 0 }
						S.Claims[window] = claim
					end
					if claim[info] or claim.count < S.Punish.per_window then
						local best = S.FindBestSequence(self, info, true)
						if best and best.Sequence.Priority >= SequencePriorities.Attack then
							if not claim[info] then
								claim[info] = true
								claim.count = claim.count + 1
							end
							return best
						end
					end
				end
			end
			return S.FindBestSequence(self, info, is_attack)
		end
	end

	-- The riposte: a pushed-back block puts the enemy in the SoftBlocking situation
	-- (CombatSituationsFunctions:TranslateBlockTypeToCombatSituation: BLOCK_TYPE_PUSHED_BACK), where
	-- attacks ignore the token and the timers (DoesSituationIgnoreTimersAndFacing: `arg1 >=
	-- CombatSituations.SoftBlocking`), but only when the style lists sequences for it. The bandit-like
	-- styles that block that way but ship none get a two-strike counter (at most every 3 s) plus their
	-- own melee list. Added at load, so CombatInfo:ApplyStyle's attack zones (CombatSequences:
	-- GetAttackZones) include it for every combatant made after.
	if CombatSequences and CombatSituations and CombatZones and CombatStyles and CombatGroupTypes
		and SequencePriorities and EScriptableAction and EBlockType then
		CombatSequences.SoulsRiposte = {
			CounterAttack = true,
			IsCombo = true,
			Priority = SequencePriorities.Attack,
			Zones = { [CombatZones.Near] = true, [CombatZones.Middle] = true },
			ZonesWhilePlaying = { [CombatZones.Near] = true, [CombatZones.Middle] = true, [CombatZones.Far] = true },
			Actions = {
				{ Type = EScriptableAction.MELEE_STRIKE, Anim = "StrikeLeft", SpeedMultiplier = 1.1 },
				{ Type = EScriptableAction.MELEE_STRIKE, Anim = "StrikeRight" },
			},
		}
		for _, style in pairs(CombatStyles) do
			if IsStyle(style) and style.Sequences and style.CanBlock and style.BlockData
				and style.CombatGroupType == CombatGroupTypes.BanditLike
				and style.BlockData.PrimaryBlockType == EBlockType.BLOCK_TYPE_PUSHED_BACK
				and style.ValidStates[CombatSituations.SoftBlocking]
				and style.Sequences[CombatSituations.SoftBlocking] == nil
				and style.Sequences[CombatSituations.Melee] then
				local list = { { "SoulsRiposte", 3 } }
				for _, entry in ipairs(style.Sequences[CombatSituations.Melee]) do
					list[#list + 1] = entry
				end
				style.Sequences[CombatSituations.SoftBlocking] = list
			end
		end
	end

	-- Circling: an enemy without the token may only play idle-priority sequences (ScoreSequence: `if
	-- arg2.IsActiveAttacker then return nil` below Movement), and for most styles those are standing
	-- (Idle) and taunting (BanditGoad). Those get a 6 s cooldown and the shipped side-steps
	-- (CombatSequences.IdleStrafeLeft / IdleStrafeRight, idle priority, Near to Far, the
	-- CombatStrafeLeft / CombatStrafeRight animations every CanStrafe style already steps with in
	-- OuterRingSpreadOut) are added, so the waiting ring moves round the hero instead of watching.
	if CombatStyles and CombatSituations and CombatSequences and CombatSequences.IdleStrafeLeft and CombatSequences.IdleStrafeRight then
		for _, style in pairs(CombatStyles) do
			local melee = IsStyle(style) and style.Sequences and style.Sequences[CombatSituations.Melee]
			if melee and style.CanStrafe and not style.NonFormation then
				local has = {}
				for i, entry in ipairs(melee) do
					local name = type(entry) == "table" and entry[1] or entry
					has[name] = true
					if name == "Idle" or name == "BanditGoad" then
						melee[i] = { name, 6 }
					end
				end
				if not has.IdleStrafeLeft then
					melee[#melee + 1] = { "IdleStrafeLeft", 2.5 }
				end
				if not has.IdleStrafeRight then
					melee[#melee + 1] = { "IdleStrafeRight", 2.5 }
				end
			end
		end
	end

	-- Boss phases: no shipped style lists UpdateEnrage (combatstates/hobbecombatstates.lua) among its
	-- states, so the Enrage tables (the Hobbe leaders' roar, damage and speed boost) are dormant. A
	-- boss-tier style that has one enrages below half health; an enraged enemy attacks without the
	-- token (CanAttackTarget) until Enrage.Duration passes (CombatInfo:Update: Combat.ClearEnrage).
	-- The Hobbe styles share CombatStyles.StandardCombatStates, so a boss style is given its own copy
	-- of its states before the state is added; CombatInfo:GetCombatStates reads them at ApplyStyle.
	-- Trolls wait less after landing a hit (BehaviourTrollAttack reads
	-- `Balance.SecondsToWaitAfterHittingTarget` from Style.Balance[health status], live).
	if CombatStyles and CombatSituations then
		for _, style in pairs(CombatStyles) do
			local states = IsStyle(style) and style.ValidStates[CombatSituations.Melee]
			if states and style.Enrage and style.SoulsTier == "boss" and CombatStates and CombatStates.UpdateEnrage then
				if not style.SoulsOwnStates then
					local own = {}
					for situation, list in pairs(style.ValidStates) do
						own[situation] = list
					end
					local melee = {}
					for i, state in ipairs(states) do
						melee[i] = state
					end
					own[CombatSituations.Melee] = melee
					style.ValidStates = own
					style.SoulsOwnStates = true
					states = melee
				end
				style.Enrage.WhenHealthBelowPercent = 50
				style.Enrage.MinSecondsBetween = 20
				style.Enrage.Duration = 10
				local after, has = nil, false
				for i, state in ipairs(states) do
					if state == "UpdateEnrage" then
						has = true
					elseif state == "WaitForActionToFinish" then
						after = i
					end
				end
				if not has then
					table.insert(states, (after or 0) + 1, "UpdateEnrage")
				end
			end
			if type(style) == "table" and style.IsTroll and type(style.Balance) == "table" then
				for _, balance in pairs(style.Balance) do
					if type(balance) == "table" and balance.SecondsToWaitAfterHittingTarget then
						balance.SecondsToWaitAfterHittingTarget = math.min(balance.SecondsToWaitAfterHittingTarget, 3)
					end
				end
			end
		end
	end
end

S.ApplyAI()
Refii.on("lua_ready", function() S.ApplyAI() end)
