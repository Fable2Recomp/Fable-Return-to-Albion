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
-- game reads by name every update are re-applied on the tick a value changes (ApplyTierCaps).

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
		S.Cast[e.caster] = Timing.GetWorldFrame()
	end
end)

function S.RecentlyCast(entity)
	if entity == nil then
		return false
	end
	local at = S.Cast[Refii.handle(entity)]
	return at ~= nil and Timing.GetSecondsSince(at) < Setting("ai_punish_cast_seconds", 1.5)
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
			elseif Timing.HaveSecondsElapsedSince(tick, wheel.ActiveAttackerTimeOut) and not Action.IsAttackingViaAction(info.Combatant) then
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
			held[pick] = Timing.GetWorldFrame()
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

-- Tiers, from the styles' own data: a leader, a style with a boss health bar or a troll is a
-- boss; an "Elite" style is elite; the rest is fodder. Each tier caps the swing gap and the
-- courtesy waits (all three are read by name on every update); a style that ships quicker keeps
-- its own number.
S.Tiers = {
	fodder = { gap_key = "ai_gap_fodder", gap_default = 2.0, hit = 1.0, attacked = 0.75, block_key = "ai_block_chance_fodder", block_default = 15 },
	elite = { gap_key = "ai_gap_elite", gap_default = 1.2, hit = 0.6, attacked = 0.4, block_key = "ai_block_chance_elite", block_default = 35 },
	boss = { gap_key = "ai_gap_boss", gap_default = 0.7, hit = 0.3, attacked = 0, block_key = "ai_block_chance_boss", block_default = 50 },
}

-- A tier's swing gap and block chance, read live by the tier's own setting key.
local function TierGap(tier)
	return Setting(S.Tiers[tier].gap_key, S.Tiers[tier].gap_default)
end

local function TierBlock(tier)
	return Setting(S.Tiers[tier].block_key, S.Tiers[tier].block_default)
end

local function TierOf(name, style)
	if style.IsLeaderType or style.UsesHealthBar or style.IsTroll then
		return "boss"
	end
	if type(name) == "string" and string.find(name, "Elite", 1, true) then
		return "elite"
	end
	return "fodder"
end

-- The caps on the styles' own numbers: applied now, and again on the tick a gap setting changes.
-- The shipped number is kept on the style the first time (SoulsShippedGap), so a wider gap after
-- a narrower one still computes from the shipped base; the courtesy waits are fixed per tier.
local function ApplyTierCaps()
	if not CombatStyles then
		return
	end
	for name, style in pairs(CombatStyles) do
		if IsStyle(style) then
			style.SoulsTier = style.SoulsTier or TierOf(name, style)
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
	end
end

local applied_gaps = { fodder = nil, elite = nil, boss = nil }

local function ApplyTierCapsIfChanged()
	local fodder, elite, boss = TierGap("fodder"), TierGap("elite"), TierGap("boss")
	if fodder ~= applied_gaps.fodder or elite ~= applied_gaps.elite or boss ~= applied_gaps.boss then
		applied_gaps.fodder, applied_gaps.elite, applied_gaps.boss = fodder, elite, boss
		ApplyTierCaps()
	end
end

ApplyTierCapsIfChanged()
Refii.on("tick", ApplyTierCapsIfChanged)

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

-- Punish windows: right after a hero drinks or eats, or swings and misses, up to two enemies in
-- reach look for one of their own attack sequences as if they held the token. Keyed by player
-- (Refii.player_of): player two's body on an online host is not a Player there.
--
-- Online, the AI runs only on the host while a swing or an item use fires where that hero is
-- played. The mod API relays both to the host, where player two's arrive as replicas
-- (Refii.event_replica()), so every machine times a window from the copy it receives. Hits land
-- on the host, so a hit landed at or after the swing cancels the whiff; a relayed swing reaches
-- the host a network moment after the joiner swung, so for one a hit up to arrival_slack before
-- its arrival counts too. Alone and in local co-op every event fires on the one machine.
S.Punish = {
	whiff_from = 0.25,
	per_window = 2,
	own_gap = 0.8,
	arrival_slack = 0.25,
}
S.Used = {}
S.Swung = {}
S.SwungRelayed = {}
S.Landed = {}
S.Claims = {}

local function HeroActed(player, times)
	if player ~= 1 and player ~= 2 then
		return
	end
	times[player] = Timing.GetWorldFrame()
	S.Claims = {}
end

Refii.on("item_used", function(e)
	HeroActed(e.player, S.Used)
end)
Refii.on("melee_swing", function(e)
	if e.player == 1 or e.player == 2 then
		S.SwungRelayed[e.player] = Refii.event_replica() or nil
	end
	HeroActed(e.player, S.Swung)
end)
Refii.on("hit_landed", function(e)
	if e.attacker_player == 1 or e.attacker_player == 2 then
		S.Landed[e.attacker_player] = Timing.GetWorldFrame()
	end
end)

-- Whether the hero's last swing has a landed hit of its own: one at or after the swing, or, for a
-- relayed swing, one up to arrival_slack before it arrived.
local function SwingLanded(player, swung)
	local landed = S.Landed[player]
	if landed == nil then
		return false
	end
	if landed >= swung then
		return true
	end
	return S.SwungRelayed[player] == true
		and Timing.GetSecondsSince(landed) - Timing.GetSecondsSince(swung) <= S.Punish.arrival_slack
end

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
	local used = S.Used[player]
	if used and Timing.GetSecondsSince(used) < Setting("ai_punish_heal_seconds", 1.6) then
		return "used" .. player .. ":" .. used
	end
	local swung = S.Swung[player]
	if swung and not SwingLanded(player, swung) then
		local age = Timing.GetSecondsSince(swung)
		if age > S.Punish.whiff_from and age < Setting("ai_punish_whiff_seconds", 1.1) then
			return "swung" .. player .. ":" .. swung
		end
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
-- fields are all 0). Each new unblocked hit now also rolls the tier's chance to raise the guard,
-- through the same call and weapon rule the shipped UpdateBlocking uses; the shipped rule then
-- runs as before and lowers the guard when the hits stop.
--
-- The shipped weapon rule compares against EBlockType.BLOCK_MELEE_DODGE, a name the enum does not
-- have (it is BLOCK_TYPE_DODGE_MELEE), so an unarmed dodger (the balverines) never dodged through
-- it and their after-dodge counters never played. The alias restores the name it meant.
if EBlockType and EBlockType.BLOCK_TYPE_DODGE_MELEE then
	EBlockType.BLOCK_MELEE_DODGE = EBlockType.BLOCK_TYPE_DODGE_MELEE
end

if CombatStates and CombatStates.UpdateBlocking then
	S.UpdateBlocking = S.UpdateBlocking or CombatStates.UpdateBlocking
	CombatStates.UpdateBlocking = function(self)
		local info = self.CombatInfo
		local style = info and info.Style
		if style and style.CanBlock and style.BlockData and style.SoulsTier
			and (info.CurrentBlock == nil or info.CurrentBlock == EBlockType.BLOCK_TYPE_NULL) then
			local hits = Block.GetNumHitsOfTypeInTime(self.Entity, EBlockType.BLOCK_TYPE_NULL, 2)
			if hits > (info.SoulsHitsSeen or 0) and TierBlock(style.SoulsTier) > GetRandomNumber(100) then
				local block = style.BlockData.PrimaryBlockType
				if info.CurrentWeaponType == WeaponTypes.Melee or block == EBlockType.BLOCK_TYPE_DODGE_MELEE
					or block == EBlockType.BLOCK_TYPE_GARTH then
					info.CurrentBlock = block
					Block.SetBlockingToType(self.Entity, block)
				end
			end
			info.SoulsHitsSeen = hits
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
