-- Souls Combat: the enemies' fighting.
--
-- A module of init.lua (require("ai")).

local S = SoulsCombat
local C = require("common")
local Setting = C.Setting

----------------------------------------------------------------------------------------------------
-- ENEMIES (ai/combatwheel.lua, combatinfo.lua, combatsequences.lua, combatstates.lua)
--
-- How the shipped AI takes turns: each defender has a CombatWheel whose inner ring holds
-- ProngLimit (3) slots round the target, everyone else waits on the outer ring. The wheel grants
-- ONE attack token (CombatWheel:SetActiveAttacker -> wheel.ActiveAttacker); an enemy swings only
-- while it holds it (CombatInfo:CanAttackTarget), gives it back after its sequence, and loses it
-- after ActiveAttackerTimeOut (3 s) without attacking. On top of that, nobody may swing for
-- SecondsToWaitAfterTargetIsHit (2-4 s) after ANYONE hit the target, nor within
-- MinSecondsBetweenMeleeAttacks of its own last sequence. NonFormation styles (hollow men,
-- trolls, banshees, beetles, Lucien's soldiers) skip the wheel and attack whenever the timers
-- allow — the game's own aggro mode, kept.
--
-- The souls pack: two tokens and four slots per defender, a short token, swing gaps by tier,
-- combos that commit, enemies that punish a heal or a whiffed swing, blocks that can come on any
-- hit and turn into a riposte, circling instead of standing, and boss enrage phases.
----------------------------------------------------------------------------------------------------

-- EVERY SETTING IS READ AT USE: the numbers below are read where they act (each wheel update,
-- each combat update, each hit), so a live tune reaches the next one; the style patches that the
-- game reads by name every update are re-applied when a gap setting changes (setting_changed).
--
-- ONE CLOCK: every time this file keeps of its own is Refii.now(); the shipped AI's own stamps
-- (CombatInfo.LastAttackTime) stay on the game's Timing, which wrote them.

-- A combat style, not one of the other tables CombatStyles also holds (StandardMeleeCombatStates,
-- ShootingBalanceDataDefault ...).
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

if CombatWheel then
	CombatWheel.ActiveAttackerTimeOut = Setting("ai_token_seconds", 1.5)

	-- ProngLimit is a literal in CombatWheel:Create, captured per wheel; restated on every update
	-- with the timeout, so a live change reaches the wheels that exist.
	S.WheelCreate = S.WheelCreate or CombatWheel.Create
	CombatWheel.Create = function(self, defender_info)
		local wheel = S.WheelCreate(self, defender_info)
		if wheel and wheel.InnerRing then
			wheel.InnerRing.ProngLimit = Setting("ai_ring_slots", 4)
		end
		return wheel
	end

	-- The second (and further) tokens: after the wheel's own grant, inner-ring attackers that are
	-- ready get a token too, longest-idle first (WheelRing:FindBestAttacker's order), up to the
	-- limit. They give it back exactly as the wheel's own holder does: after their sequence
	-- (PlayCombatSequence clears IsActiveAttacker) or after the timeout without attacking.
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
		-- enemy still out of reach too, which then closes in (a holder moves to its slot).
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

-- Closing in: within EnterStrafeDist of its target (5 m unless the style says otherwise, copied
-- by ApplyStyle) an enemy stops sprinting and moves to its slot with strafe actions at the
-- style's StrafeActionSpeed (2-2.5 m/s) — slower than a locked-on hero backs off (4.25), so a
-- step back kept every swing out of reach. A token holder now sprints to within
-- ai_close_in_distance before it strafes; without a token the style's own distance stands.
if CombatInfo and CombatInfo.Update then
	S.CombatInfoUpdate = S.CombatInfoUpdate or CombatInfo.Update
	CombatInfo.Update = function(self, ...)
		if self.SoulsTierStyle ~= self.Style and S.NoteCombatantTier then
			S.NoteCombatantTier(self)
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

-- The shipped anti-spam rule (CombatStates:AntiSpamCheck, in every melee style's states): when
-- SpellsToTrigger spells (or ShotsToTrigger shots) hit an enemy within TimeToCheckFor seconds,
-- its group breaks formation and everyone attacks freely for TimeToAggroFor. Shipped 4 in 4 s,
-- on the bandit families only; now 2 spells or 3 shots in 6 s, on every style in a group.
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

-- Tiers: a leader, a style with a boss health bar or a troll is a boss, from the styles' own
-- data; a creature whose definition states it elite or a lieutenant (Refii.creature_tier:
-- CREATURE_ELITE, CREATURE_LIEUTENANT) is elite; the rest is fodder. Each tier caps the swing gap
-- and the courtesy waits, which the shipped AI reads off the STYLE by name on every update: a
-- style starts as boss or fodder, and becomes elite on the first update of an elite creature
-- fighting with it. A style that ships quicker keeps its own number. The block chance is the
-- combatant's own tier.
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
-- courtesy waits are fixed per tier.
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

-- Each combatant's tier, on its first update with a style (CombatInfo.Update above); a style
-- fought with by a higher tier than it holds is raised to it.
function S.NoteCombatantTier(info)
	info.SoulsTierStyle = info.Style
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

-- Combos commit: an interruptible sequence is broken off (Action.AttemptComboBreak) whenever the
-- target leaves its zones or a better sequence appears; an Uninterruptable one plays out, so a
-- roll away leaves the enemy finishing its string — the opening.
if CombatSequences and SequencePriorities then
	for _, sequence in pairs(CombatSequences) do
		if type(sequence) == "table" and sequence.Actions and sequence.IsCombo and not sequence.CounterAttack
			and sequence.Priority and sequence.Priority >= SequencePriorities.Attack then
			sequence.Uninterruptable = true
		end
	end
end

-- Punish windows: right after a hero heals, or ends a swing that hit nothing, up to two enemies in
-- reach look for one of their own attack sequences as if they held the token. Keyed by player
-- (Refii.player_of): player two's body on an online host is not a Player there.
--   health_changed — a hero's health rose (delta > 0): a potion, food, any heal the game's own
--     Modify made. Online the game runs it on both machines; the AI's machine times the window
--     from its own run, replica or not.
--   melee_swing_ended — the hero's strike ended with `hits` 0: a whiff. Relayed, so the host,
--     where the AI runs, hears player two's from the joiner. The joiner counts the host's hits
--     relayed to it only while its strike is still current, so a late one is missed and a
--     relayed whiff may be a hit: on a relayed copy, a hit by that hero landed here (the host
--     delivers every hit, hit_landed) since the hero's previous strike ended cancels the whiff.
--     The window opens when the strike ends, where the whiff is stated, and lasts
--     ai_punish_whiff_seconds.
S.Punish = {
	per_window = 2,
	own_gap = 0.8,
}
S.Healed = {}
S.Whiffed = {}
S.StrikeEnded = {}
S.Landed = {}
S.Claims = {}

local function HeroActed(player, times)
	times[player] = Refii.now()
	S.Claims = {}
end

Refii.on("health_changed", function(e)
	if (e.player == 1 or e.player == 2) and e.delta > 0 then
		HeroActed(e.player, S.Healed)
	end
end)

Refii.on("hit_landed", function(e)
	if (e.attacker_player == 1 or e.attacker_player == 2) and not Refii.event_replica() then
		S.Landed[e.attacker_player] = Refii.now()
	end
end)

Refii.on("melee_swing_ended", function(e)
	local p = e.player
	if p ~= 1 and p ~= 2 then
		return
	end
	local previous = S.StrikeEnded[p]
	S.StrikeEnded[p] = Refii.now()
	if e.hits > 0 then
		return
	end
	local landed = S.Landed[p]
	if Refii.event_replica() and landed ~= nil and (previous == nil or landed >= previous) then
		return
	end
	HeroActed(p, S.Whiffed)
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

if CombatSequences then
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

-- Blocks: the shipped styles block only once N unblocked hits land within a window (their chance
-- fields are all 0). Each new hit that lands while the enemy holds no guard now also rolls its
-- tier's chance to raise the guard, through the same call and weapon rule the shipped
-- UpdateBlocking uses; the shipped rule then runs as before and lowers the guard when the hits
-- stop. The hit is the hit_landed event, decided where the AI runs (a relayed copy is skipped),
-- kept on the victim (Refii.entity_value) until its next blocking update rolls for it, if that
-- comes within the 2 s the shipped rule counts hits over.
--
-- The shipped weapon rule compares against EBlockType.BLOCK_MELEE_DODGE, a name the enum does not
-- have (it is BLOCK_TYPE_DODGE_MELEE), so an unarmed dodger (the balverines) never dodged through
-- it and their after-dodge counters never played. The alias restores the name it meant; a fix in
-- the core, for every mod, is separate.
if EBlockType and EBlockType.BLOCK_TYPE_DODGE_MELEE then
	EBlockType.BLOCK_MELEE_DODGE = EBlockType.BLOCK_TYPE_DODGE_MELEE
end

local kUnansweredHitKey = "souls_combat.unanswered_hit"
local kHitMemorySeconds = 2

Refii.on("hit_landed", function(e)
	if e.victim_player == 0 and e.victim ~= nil and e.victim ~= 0 and not Refii.event_replica() then
		Refii.entity_set(e.victim, kUnansweredHitKey, Refii.now())
	end
end)

if CombatStates and CombatStates.UpdateBlocking then
	S.UpdateBlocking = S.UpdateBlocking or CombatStates.UpdateBlocking
	CombatStates.UpdateBlocking = function(self)
		local info = self.CombatInfo
		local style = info and info.Style
		local tier = info and (info.SoulsTier or style and style.SoulsTier)
		if style and style.CanBlock and style.BlockData and tier
			and (info.CurrentBlock == nil or info.CurrentBlock == EBlockType.BLOCK_TYPE_NULL) then
			local entity = Refii.handle(self.Entity)
			local hit_at = Refii.entity_value(entity, kUnansweredHitKey)
			if hit_at ~= nil then
				Refii.entity_set(entity, kUnansweredHitKey, nil)
				if Refii.now() - hit_at < kHitMemorySeconds and TierBlock(tier) > GetRandomNumber(100) then
					local block = style.BlockData.PrimaryBlockType
					if info.CurrentWeaponType == WeaponTypes.Melee or block == EBlockType.BLOCK_TYPE_DODGE_MELEE
						or block == EBlockType.BLOCK_TYPE_GARTH then
						info.CurrentBlock = block
						Block.SetBlockingToType(self.Entity, block)
					end
				end
			end
		end
		return S.UpdateBlocking(self)
	end
end

-- The riposte: a pushed-back block opens the soft-blocking situation for 1.5 s, in which attacks
-- ignore the token and the timers. The bandit-like styles that can block that way but ship no
-- sequences for it get a two-strike counter (at most every 3 s) plus their own melee list. Added
-- before any combatant exists, so CombatInfo:New's attack zones include it.
if CombatSequences and CombatSituations and CombatZones then
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
	for _, style in pairs(CombatStyles or {}) do
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

-- Circling: an enemy without the token may only play idle-priority sequences, and for most
-- styles those are standing (Idle) and taunting (BanditGoad). Those get a 6 s cooldown and the
-- shipped strafes are added, so the waiting ring moves round the hero instead of watching.
if CombatStyles and CombatSituations then
	for _, style in pairs(CombatStyles) do
		local melee = IsStyle(style) and style.Sequences and style.Sequences[CombatSituations.Melee]
		if melee and style.CanStrafe and style.UseStrafeActions and not style.NonFormation then
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

-- Boss phases: no shipped style lists UpdateEnrage among its states, so the Enrage tables (the
-- Hobbe leaders' roar, damage and speed boost) are dormant. A boss-tier style that has one enrages
-- below half health; an enraged enemy attacks without the token. Trolls wait less after landing a
-- hit (their Balance, per health tier, is read live).
if CombatStyles and CombatSituations then
	for _, style in pairs(CombatStyles) do
		local states = IsStyle(style) and style.ValidStates[CombatSituations.Melee]
		if states and style.Enrage and style.SoulsTier == "boss" then
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
