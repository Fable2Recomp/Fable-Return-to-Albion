-- Souls Combat: the hero's movement and the enemies' fighting.
--
-- Runs once per game session, after the shipped scripts have defined their tables and before the
-- hero or any combatant exists. Everything here patches those tables or wraps their methods;
-- nothing replaces a shipped file. Numbers come from settings.json through Refii.setting.

SoulsCombat = SoulsCombat or {}
local S = SoulsCombat

local function Setting(name, default)
	return Refii.setting("souls_combat." .. name, default)
end

----------------------------------------------------------------------------------------------------
-- MOVEMENT (miscellaneous/herolocomotionstates.lua)
--
-- The player-control component copies a HeroLocomotionData* table by name each time it loads one
-- (sub_8283E408: at creation, indoors/outdoors, a movement-mode change), and the state tables are
-- parsed once, on the first locomotion activation (sub_827F7A78 -> sub_827F3598). Both happen
-- after this runs.
--
-- Turning: the facing turns toward the stick at a rate blended from MaxTurnSpeedStatic (standing)
-- to MaxTurnSpeedRunning (at full speed), degrees per second (sub_822B1F78). Shipped: 350 and
-- 115, so a running hero swings round in a wide arc. Speed follows the stick through a first-order
-- lag, FrictionCoefficient per second (4: about a quarter of the gap per update).
--
-- Turn-in-place: standing still, the Idle state's own MovementParams cap turning at 1 deg/s and
-- a stick behind the hero plays a Turn90/Turn180 animation; running, a reversal plays
-- Turn180IntoRun/RunTurn180. The state picker takes the first valid entry of the current state's
-- `States` list and skips a name it does not find, so those turns are taken out of every list,
-- LocomoteForwards accepts any stick direction (shipped +-120 deg) and Idle may turn: the hero
-- pivots on the spot.
----------------------------------------------------------------------------------------------------

S.TurnSpeedStatic = Setting("turn_speed_static", 720)
S.TurnSpeedRunning = Setting("turn_speed_running", 540)
S.Friction = Setting("move_friction", 9)

for _, name in ipairs({ "HeroLocomotionData", "HeroLocomotionDataIndoorsDefault", "HeroLocomotionDataChild", "HeroLocomotionDataChildIndoorsDefault" }) do
	local data = _G[name]
	if type(data) == "table" then
		data.MaxTurnSpeedStatic = math.max(data.MaxTurnSpeedStatic or 0, S.TurnSpeedStatic)
		data.MaxTurnSpeedRunning = math.max(data.MaxTurnSpeedRunning or 0, S.TurnSpeedRunning)
		data.FrictionCoefficient = math.max(data.FrictionCoefficient or 0, S.Friction)
	end
end

local turn_states = {
	HeroLocomotionStateTurn90CW = true,
	HeroLocomotionStateTurn90ACW = true,
	HeroLocomotionStateTurn180 = true,
	HeroLocomotionStateTurn90IntoRunCW = true,
	HeroLocomotionStateTurn90IntoRunACW = true,
	HeroLocomotionStateTurn180IntoRun = true,
	HeroLocomotionStateRunTurn180 = true,
	HeroLocomotionStateCircleTurn360 = true,
	HeroLocomotionStateOutOfRunTurn90CW = true,
	HeroLocomotionStateOutOfRunTurn90ACW = true,
	HeroLocomotionStateOutOfRunTurn180CW = true,
	HeroLocomotionStateOutOfRunTurn180ACW = true,
}

for name, state in pairs(_G) do
	if type(name) == "string" and type(state) == "table" and string.sub(name, 1, 19) == "HeroLocomotionState"
		and not turn_states[name] and type(state.States) == "table" then
		local kept = {}
		for _, next_state in ipairs(state.States) do
			if not turn_states[next_state] then
				kept[#kept + 1] = next_state
			end
		end
		-- Never strand a state: one with no way out keeps its list.
		if #kept > 0 then
			state.States = kept
		end
	end
end

if HeroLocomotionStateLocomoteForwards and HeroLocomotionData then
	local min_control = HeroLocomotionData.MinControlMagForMovement
	HeroLocomotionStateLocomoteForwards.ControlArea = { CVector3(min_control, -180.1, 0), CVector3(1.05, 180.1, 0) }
end

if HeroLocomotionStateIdle and HeroLocomotionStateIdle.MovementParams then
	HeroLocomotionStateIdle.MovementParams.MaxTurnSpeedStatic = S.TurnSpeedStatic
	HeroLocomotionStateIdle.MovementParams.MaxTurnSpeedRunning = S.TurnSpeedStatic
end

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

S.TokensPerDefender = Setting("ai_attackers_at_once", 2)
S.InnerRingSlots = Setting("ai_ring_slots", 4)

-- A combat style, not one of the other tables CombatStyles also holds (StandardMeleeCombatStates,
-- ShootingBalanceDataDefault ...).
local function IsStyle(style)
	return type(style) == "table" and style.ValidStates ~= nil and style.Limits ~= nil
end

-- Casting is an opening. Each hero's last spell launch (the spell_launched event, after the
-- spell really exists), by handle.
S.CastWindow = Setting("ai_punish_cast_seconds", 1.5)
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
	return at ~= nil and Timing.GetSecondsSince(at) < S.CastWindow
end

if CombatWheel then
	CombatWheel.ActiveAttackerTimeOut = Setting("ai_token_seconds", 1.5)

	-- ProngLimit is a literal in CombatWheel:Create, captured per wheel.
	S.WheelCreate = S.WheelCreate or CombatWheel.Create
	CombatWheel.Create = function(self, defender_info)
		local wheel = S.WheelCreate(self, defender_info)
		if wheel and wheel.InnerRing then
			wheel.InnerRing.ProngLimit = S.InnerRingSlots
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
		local limit = S.TokensPerDefender
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
S.CloseInDistance = Setting("ai_close_in_distance", 3)

if CombatInfo and CombatInfo.Update then
	S.CombatInfoUpdate = S.CombatInfoUpdate or CombatInfo.Update
	CombatInfo.Update = function(self, ...)
		if self.EnterStrafeDist ~= nil then
			if self.SoulsStrafeDist == nil or self.SoulsStrafeDistFrom ~= self.Style then
				self.SoulsStrafeDist = self.EnterStrafeDist
				self.SoulsStrafeDistFrom = self.Style
			end
			if self.IsActiveAttacker then
				self.EnterStrafeDist = math.min(self.SoulsStrafeDist, S.CloseInDistance)
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
	fodder = { gap = Setting("ai_gap_fodder", 2.0), hit = 1.0, attacked = 0.75, block = Setting("ai_block_chance_fodder", 15) },
	elite = { gap = Setting("ai_gap_elite", 1.2), hit = 0.6, attacked = 0.4, block = Setting("ai_block_chance_elite", 35) },
	boss = { gap = Setting("ai_gap_boss", 0.7), hit = 0.3, attacked = 0, block = Setting("ai_block_chance_boss", 50) },
}

local function TierOf(name, style)
	if style.IsLeaderType or style.UsesHealthBar or style.IsTroll then
		return "boss"
	end
	if type(name) == "string" and string.find(name, "Elite", 1, true) then
		return "elite"
	end
	return "fodder"
end

if CombatStyles then
	for name, style in pairs(CombatStyles) do
		if IsStyle(style) then
			style.SoulsTier = TierOf(name, style)
			local tier = S.Tiers[style.SoulsTier]
			if style.MinSecondsBetweenMeleeAttacks ~= nil then
				style.MinSecondsBetweenMeleeAttacks = math.min(style.MinSecondsBetweenMeleeAttacks, tier.gap)
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
-- reach look for one of their own attack sequences as if they held the token.
S.Punish = {
	heal = Setting("ai_punish_heal_seconds", 1.6),
	whiff_from = 0.25,
	whiff_to = Setting("ai_punish_whiff_seconds", 1.1),
	per_window = 2,
	own_gap = 0.8,
}
S.Used = {}
S.Swung = {}
S.Landed = {}
S.Claims = {}

Refii.on("item_used", function(e)
	if e.player ~= 0 then
		S.Used[e.entity] = Timing.GetWorldFrame()
		S.Claims = {}
	end
end)
Refii.on("melee_swing", function(e)
	if e.player ~= 0 then
		S.Swung[e.entity] = Timing.GetWorldFrame()
		S.Claims = {}
	end
end)
Refii.on("hit_landed", function(e)
	if e.attacker_player ~= 0 then
		S.Landed[e.attacker] = Timing.GetWorldFrame()
	end
end)

local function PunishWindow(info)
	local target = info.Target
	if target == nil or not Player.IsAvailable(target) then
		return nil
	end
	if info.Zone ~= CombatZones.Near and info.Zone ~= CombatZones.Middle then
		return nil
	end
	if info.LastAttackTime and Timing.GetSecondsSince(info.LastAttackTime) < S.Punish.own_gap then
		return nil
	end
	local hero = Refii.handle(target)
	local used = S.Used[hero]
	if used and Timing.GetSecondsSince(used) < S.Punish.heal then
		return "used" .. used
	end
	local swung = S.Swung[hero]
	if swung and (S.Landed[hero] or -1) < swung then
		local age = Timing.GetSecondsSince(swung)
		if age > S.Punish.whiff_from and age < S.Punish.whiff_to then
			return "swung" .. swung
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
			if hits > (info.SoulsHitsSeen or 0) and S.Tiers[style.SoulsTier].block > GetRandomNumber(100) then
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

----------------------------------------------------------------------------------------------------
-- THE HERO'S PARRY (miscellaneous/combatbalance.lua). The player copies the table when it is
-- created, after this runs (sub_823E9BF0), and the parry reads the copy by name (sub_82986B28 ->
-- sub_829897B0): a quarter-second window instead of a full second, in a 90-degree wedge.
----------------------------------------------------------------------------------------------------
if HeroCombatBalanceParams then
	HeroCombatBalanceParams.InputWindowSecondsForParry = Setting("parry_window_seconds", 0.25)
	HeroCombatBalanceParams.MeleeParryWedgeAngle = 90
end
