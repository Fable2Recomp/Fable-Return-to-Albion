-- Spell Weaving — two spells from one release, Fable 3's gauntlets in Fable 2. Pure Lua on the
-- mod API (Godot/docs/modding/): no native code, no raw guest read, no hook of its own.
--
-- THE PAIR. Of the slot's spell and its partner, the higher in the hierarchy is the BASE and is
-- the one spell launched: `spell_launch` carries the slot's type writable for that launch, and
-- when the slot holds the lower one the type becomes the base's, so position, target, targeted /
-- area and the launch notification (where Souls Combat charges mana) are all the game's. The lower
-- spell, the OVERLAY, is never launched; it is cast by the base's hits: a real Targeted cast of
-- the partner on each victim the base hits (`hit_landed`), a short delay later, as Fable III's
-- primary issues its second cast. A cast weaves only up to the Spell Weaving level: level N
-- weaves power N and below.
--
-- Spell types: 0 Lightning, 1 Fireball, 2 SlowTime, 3 Swords, 4 Vortex, 5 Chaos, 6 ForcePush,
-- 7 DeadRising. Slow Time and Raise Dead are never partners. Learned ranks: ability 7 + type.
--
-- EACH PLAYER'S CHOICES LIVE IN THE PLAYER STORE, SAVED with the hero, the same on both co-op
-- machines:
--   spell_weaving.ability        the Spell Weaving level, 0..5: the ENGINE buys and unlearns it
--                                from ui/abilities.json (price, refund, saves from before)
--   spell_weaving.partner_1..5   the partner picked for each slot, -1 for none: the ENGINE's spell
--                                pick of ui/spellwheel.json writes it (this file refuses Slow Time
--                                and Raise Dead on `spell_pick`, so no surface can store one)
-- and, stated where the hero is played (session values): spell_weaving.partner_active_1..5 (what
-- is in effect per slot: chosen, learned, within the level) and spell_weaving.hud_partner (the
-- glyph while charging, ui/hud.json).
--
-- Settings (settings.json, Refii.setting):
--   spell_weaving.top_only       1: only the charged slot casts (Fable 3); 0: Fable 2's cascade
--   spell_weaving.allow_same     1: a partner equal to the slot's spell casts it twice
--   spell_weaving.woven          1: weaving on; 0: every slot casts its own spell alone
--   spell_weaving.woven_cooldown_seconds, shot_delay, shot_delay_vortex, weave_damage,
--   spell_weaving.sword_tint_<fire|shock|chaos|push>_<r|g|b>
--
-- Handles, not addresses: every entity and spell object here is the handle the events hand over;
-- a serial is compared, never computed.

SpellWeaving = SpellWeaving or {}
local W = SpellWeaving

local function Knob(key, default)
	return Refii.setting("spell_weaving." .. key, default)
end

local kMaxLevel = 5
local kSlots = 5
local kLevels = 5
local kFirstSpellAbility = 7

local kLightning = 0
local kFireball = 1
local kSlowTime = 2
local kSwords = 3
local kVortex = 4
local kChaos = 5
local kForcePush = 6
local kDeadRising = 7

-- THE HIERARCHY: Vortex > Blades > Fireball > Shock > Force Push > Chaos — Fable 3's own primaries
-- (the vortex casts its partner per creature it holds, a blade per stab, a fireball on impact). Of
-- a woven pair the higher spell is the BASE, the one cast; the lower is the OVERLAY, cast on the
-- base's victims and applied onto the base's look. Lower rank is higher.
local kRank = { [0] = 3, [1] = 2, [2] = 99, [3] = 1, [4] = 0, [5] = 5, [6] = 4, [7] = 99 }

local function Weavable(t)
	return type(t) == "number" and t >= 0 and t <= 7 and t ~= kSlowTime and t ~= kDeadRising
end

local function IsPlayer(p)
	return p == 1 or p == 2
end

----------------------------------------------------------------------------------------------------
-- THE PLAYER'S CHOICES (the player store)
----------------------------------------------------------------------------------------------------

local kAbilityKey = "spell_weaving.ability"

-- A player's Spell Weaving level, 0..kMaxLevel.
local function Level(p)
	local level = Refii.player_value(p, kAbilityKey, 0)
	if level < 0 then return 0 end
	if level > kMaxLevel then return kMaxLevel end
	return level
end

-- The player value holding a slot's partner — the pick's store (ui/spellwheel.json).
local function PartnerKey(slot)
	return "spell_weaving.partner_" .. (slot + 1)
end

-- What is in effect on a slot, stated where the hero is played (the selectors' "inEffect").
local function ActiveKey(slot)
	return "spell_weaving.partner_active_" .. (slot + 1)
end

-- A player's chosen partner on a slot, -1 for none.
local function Partner(p, slot)
	local t = Refii.player_value(p, PartnerKey(slot), -1)
	if Weavable(t) then return t end
	return -1
end

local function HasPartnerValue(p, slot)
	return Refii.player_value(p, PartnerKey(slot)) ~= nil
end

-- The slot's partner if the hero has learned at least one rank of it, else -1.
local function LearnedPartner(p, hero, slot)
	local t = Partner(p, slot)
	if not Weavable(t) then return -1 end
	local rank = Refii.ability_level(hero, kFirstSpellAbility + t)
	if rank ~= nil and rank > 0 then return t end
	return -1
end

-- The partner pick (the engine's, for ui/spellwheel.json's "spell_weaving.partner"): Slow Time
-- and Raise Dead can never be woven, so a pick of either is refused, whatever surface made it.
-- The engine stores the pick after this.
Refii.on("spell_pick", function(e)
	if e.pick ~= "spell_weaving.partner" or e.spell < 0 or Weavable(e.spell) then return end
	Refii.log("Spell Weaving: " .. e.spell .. " cannot be a partner; player " .. e.player .. " slot " .. (e.slot + 1) ..
		" keeps " .. e.previous)
	return true
end)

-- A save from before keeps the choices in its Lua table (RefiiModSave.SpellWeavingPartner1..5,
-- Partner2_1..5). The Lua and save_loaded events say the table changed; the read waits for the
-- next tick with the hero present.
local reread = true

Refii.on("lua_ready", function() reread = true end)
Refii.on("save_loaded", function() reread = true end)

local function LegacyKey(p, slot)
	return "SpellWeavingPartner" .. (p == 1 and "" or "2_") .. (slot + 1)
end

-- For each player played here, a slot the hero's saved values leave empty takes the old Lua
-- table's choice. Run after every Lua rebuild and save load, so it never races the save's own values.
local function ReadSaved()
	for p = 1, 2 do
		for slot = 0, kSlots - 1 do
			if Refii.player_here(p) and not HasPartnerValue(p, slot) then
				local saved = RefiiModSave and RefiiModSave[LegacyKey(p, slot)]
				if type(saved) == "number" and saved >= 0 and saved <= 7 and Weavable(saved) then
					Refii.player_save(p, PartnerKey(slot), saved)
					Refii.log("Spell Weaving: player " .. p .. " slot " .. (slot + 1) .. " partner " .. saved ..
						" taken from the save's Lua table")
				end
			end
		end
	end
end

-- What is in effect per slot (chosen, learned, and the slot within the Spell Weaving level),
-- stated where the hero is played: the selectors draw the chosen pick faint when it is not this.
local function PublishActive()
	for p = 1, 2 do
		local hero = Refii.hero(p)
		if hero and Refii.player_here(p) then
			local level = Level(p)
			for slot = 0, kSlots - 1 do
				local active = -1
				if slot < level then active = LearnedPartner(p, hero, slot) end
				Refii.player_set(p, ActiveKey(slot), active)
			end
		end
	end
end

----------------------------------------------------------------------------------------------------
-- THE HUD GLYPH (ui/hud.json): while a hero charges, the partner the charged slot would weave is
-- stated in that player's store as spell_weaving.hud_partner (-1 when none, or not charging).
-- The charge level (the top slot, 0..4) arrives every frame of the charge through
-- spell_charge_level and goes stale a third of a second after the last one; a launch ends it at
-- once. The glyph shows what the launch would weave: the slot's own spell must be weavable and
-- differ from the partner (allow_same aside), read from the slot table as the launch reads it.
----------------------------------------------------------------------------------------------------

local kChargeHoldSeconds = 0.3
local charging_slot = { [1] = -1, [2] = -1 }
local charging_age = { [1] = 99, [2] = 99 }

Refii.on("spell_charge_level", function(e)
	if IsPlayer(e.player) then
		charging_slot[e.player] = e.level
		charging_age[e.player] = 0
	end
end)

Refii.on("spell_launched", function(e)
	if IsPlayer(e.player) then
		charging_slot[e.player] = -1
		charging_age[e.player] = 99
	end
end)

local function PublishHudPartner(seconds)
	for p = 1, 2 do
		charging_age[p] = charging_age[p] + seconds
		local hero = Refii.hero(p)
		if hero and Refii.player_here(p) then
			local slot = charging_slot[p]
			local partner = -1
			if slot >= 0 and slot < kSlots and charging_age[p] <= kChargeHoldSeconds and slot < Level(p) and Knob("woven", 1) ~= 0 then
				local primary = Refii.spell_in_slot(p, slot)
				partner = LearnedPartner(p, hero, slot)
				if not Weavable(primary) or (primary == partner and Knob("allow_same", 0) == 0) then
					partner = -1
				end
			end
			Refii.player_set(p, "spell_weaving.hud_partner", partner)
		end
	end
end

----------------------------------------------------------------------------------------------------
-- ONE WEAVE PER CAST. Every woven release is its own record from its launch until its base has
-- finished: the base's serial (what its hits carry), the pair, the power, and what the look rides
-- on — the vortex entity, the fireball's shot, each of the Blades' swords. Two vortices from two
-- slots keep two partners; a slot re-picked between casts changes nothing already out.
----------------------------------------------------------------------------------------------------

local kMaxWeaves = 8
local kMaxLooks = 10
local kMaxVictims = 8

local clock = 0
local weaves = {}

-- The pair the next created base spell of each player is, armed by the launch; the vortex stated
-- inside the factory, before the spell object is reported (vortex_started carries the serial).
local armed = {}

local function ArmWeave(p, base, overlay, level)
	if not IsPlayer(p) then return end
	armed[p] = { base = base, overlay = overlay, level = level, vortex_serial = nil, vortex_entity = 0 }
end

local function NewWeave(serial, p, base, overlay)
	return {
		serial = serial, spell = 0, player = p, base = base, overlay = overlay, level = 0,
		anchor = 0, vortex_live = false, swords = {}, swords_read = false, sword_tinted = {},
		origin = { 0, 0, 0 }, victims = {}, age = 0, done_at = -1, looks = {},
	}
end

local function WeaveBySerial(serial)
	if serial == nil or serial == 0 then return nil end
	for i = 1, #weaves do
		if weaves[i].serial == serial then return weaves[i] end
	end
	return nil
end

-- How long a base's look holds after the base finished (or, for an instant base, after the cast).
local kHoldSeconds = { [kLightning] = 1.5, [kForcePush] = 1.5, [kChaos] = 6.0, [kFireball] = 1.5 }

local function HoldSeconds(base)
	return kHoldSeconds[base] or 0.75
end

-- Ends every play of a weave's look.
local function StopLooks(w)
	for i = 1, #w.looks do
		if w.looks[i].id ~= 0 then Refii.stop_fx(w.looks[i].id) end
	end
	w.looks = {}
end

local function RemoveWeave(index)
	StopLooks(weaves[index])
	for i = index, #weaves - 1 do weaves[i] = weaves[i + 1] end
	weaves[#weaves] = nil
end

-- A record for a new weave: a free one, else the oldest weave's, its looks stopped.
local function AddWeave(serial, p, base, overlay)
	if #weaves >= kMaxWeaves then
		local oldest = 1
		for i = 2, #weaves do
			if weaves[i].age > weaves[oldest].age then oldest = i end
		end
		RemoveWeave(oldest)
	end
	local w = NewWeave(serial, p, base, overlay)
	weaves[#weaves + 1] = w
	return w
end

-- The live swords of a Blades weave: what the cast's controller still holds, as spell_parts
-- validates them (a sword dies 0.35 s after its stab); the last answer while the cast has aged out
-- of the factory's memory, trimmed to the swords still in the world.
local function LiveSwords(w)
	if w.swords_at == clock then return w.swords end  -- read once per tick
	w.swords_at = clock
	local parts = Refii.spell_parts(w.serial)
	if parts then
		w.swords = parts.swords or {}
		return w.swords
	end
	local kept = {}
	for i = 1, #w.swords do
		if Refii.alive(w.swords[i]) then kept[#kept + 1] = w.swords[i] end
	end
	w.swords = kept
	return kept
end

-- Whether the weave's base is still out: the vortex live, a sword hovering, the fireball not yet
-- detonated, an instant cast within its hold.
local function BaseLive(w)
	if w.base == kVortex then
		return w.vortex_live
	elseif w.base == kSwords then
		if not w.swords_read then return w.age < 3.0 end  -- the Start has not stated the swords yet
		return #LiveSwords(w) > 0
	elseif w.base == kFireball then
		return w.done_at < 0 and w.age < 6.0
	end
	local hold = HoldSeconds(w.base)
	for i = 1, #w.victims do
		if clock - w.victims[i].at <= hold then return true end
	end
	return w.done_at < 0 and w.age < 1.0
end

-- A victim the base hit, kept (newest over oldest) for the looks that ride on victims.
local function NoteVictim(w, victim)
	if victim == nil or victim == 0 then return end
	local slot = 1
	for i = 1, #w.victims do
		if w.victims[i].e == victim then
			w.victims[i].at = clock
			return
		end
		if w.victims[i].at < w.victims[slot].at then slot = i end
	end
	if #w.victims < kMaxVictims then slot = #w.victims + 1 end
	w.victims[slot] = { e = victim, at = clock }
end

----------------------------------------------------------------------------------------------------
-- THE PARTNER CAST. Once per victim per base cast, never twice within the cooldown; queued for
-- Fable III's delay and cast by the game's own factory with the hero as caster.
----------------------------------------------------------------------------------------------------

-- One partner already queued or cast for a victim by one base cast.
local recent = {}
local recent_next = 1
local kRecent = 64

local function Fresh(victim, serial, t)
	local hold = Knob("woven_cooldown_seconds", 0.5)
	for i = 1, #recent do
		local r = recent[i]
		if r.victim == victim and r.type == t then
			if r.serial == serial or r.until_ > clock then return false end
			r.serial = serial
			r.until_ = clock + hold
			return true
		end
	end
	recent[recent_next] = { victim = victim, serial = serial, type = t, until_ = clock + hold }
	recent_next = recent_next % kRecent + 1
	return true
end

-- Fable 3: value * (1 + 0.1 * (rand - 0.5)) — within five per cent either way. The generator's
-- state stays under 2^24, exact in this Lua's 32-bit float.
local rand = 12345

local function Jitter()
	rand = (rand * 75 + 74) % 65537
	return 1 + 0.1 * (rand / 65537 - 0.5)
end

-- The partner casts waiting for their delay: on a victim, or — a fireball that hit nothing alive —
-- at a point, facing a direction, each carrying where its base stands (a push is away from there).
local pending = {}
local kPending = 24

-- Partner casts queued so far: a base's moment that hit nobody is told by this not moving.
local queued = 0

-- The serials of this mod's own partner casts, the last 32: their hits are the weave side
-- (IsWeaveSerial), and none of them ever binds as a base or casts a partner of its own.
local weave_serials = {}
local weave_next = 1
local kWeaveSerials = 32

local function IsWeaveSerial(serial)
	if serial == nil or serial == 0 then return false end
	for i = 1, #weave_serials do
		if weave_serials[i] == serial then return true end
	end
	return false
end

local function NoteWeaveSerial(serial)
	if serial == nil or serial == 0 or IsWeaveSerial(serial) then return end
	weave_serials[weave_next] = serial
	weave_next = weave_next % kWeaveSerials + 1
end

-- The partner cast in progress (CastPartner): its hits land inside the factory, before the cast
-- returns its serial, so they are known by caster and type while it runs.
local casting = nil

-- THE PARTNER SPELL, cast by the game's own factory with the hero as caster: a Targeted cast on
-- the victim, or — a fireball that hit nothing alive — on no target at the impact point.
--
-- A POSITION CAST, as Fable III's weave is: every request there is "shot from the caster" = 0, and
-- Lightning, Fireball and Force Push resolve at once at the request position with the hit on the
-- target (Fable III Lightning Start sub_82B02B10 @0x82b02b58, Fireball Start sub_82AF7F60
-- @0x82af8110, Force Push Start sub_82AFFB60 @0x82affbd8) — no bolt from the caster, no projectile,
-- no hand effect: the pair reads as ONE spell whose hits carry the second effect.
--
-- Fable 2's targeted classes place from v1 (spell+0x30) along v2 (+0x60) with the target at +0x70:
-- Lightning (sub_827407F0) draws its bolt from v1 to a point on the target, Fireball (sub_82744AE0)
-- spawns its shot at v1, Force Push (sub_828B2CC8) pushes the target away from v1. So the position
-- cast is built by starting the partner ON the victim (Refii.cast_spell's pos / dir):
--   Lightning: v1 = the victim — a zero-length bolt, the strike's impact on the victim only.
--   Fireball: v1 = the victim — the shot detonates where it spawns.
--   Force Push: v1 = the BASE's own place (the vortex, the fireball's impact, the stab), so the
--     victim is shoved away from the base, as Fable III's request direction has it.
--   Vortex, Blades, Chaos: at the victim.
-- With no victim (a fireball that struck nothing alive) the cast has no target, and an untargeted
-- Lightning draws v1 + 15 x v2 then rolls a 50% re-acquire of a random creature within 1.35 of
-- v1's height: it starts 15 above the point striking straight down, so the re-acquire finds
-- nobody and the strike lands where the fireball did.
--
-- The cast's serial comes back with the handle. A hit that lands inside the factory (a targeted
-- Lightning's bolt) reaches the `hit` handler LIVE, nested inside this call, before the serial is
-- known: `casting` names the cast while it runs, and the handler notes the serial it carries. The
-- cast's own spell_created and the bolt's hit_landed reach this chunk deferred, after it returns,
-- and find the serial noted.
local function CastPartner(cast)
	local hero = Refii.hero(cast.player)
	if hero == nil or (cast.victim ~= 0 and not Refii.alive(cast.victim)) then return end
	local at_point = cast.victim == 0
	local target = { cast.where[1], cast.where[2], cast.where[3] }
	if not at_point then
		local x, y, z = Refii.position(cast.victim)
		if x == nil then return end
		target = { x, y, z }
	end
	-- The request direction: from the base to the victim (Fable III: creature - vortex, impact -
	-- spell), the cast's own facing when the base stands on the victim.
	local origin = cast.origin
	local dir = { target[1] - origin[1], target[2] - origin[2], target[3] - origin[3] }
	local length = math.sqrt(dir[1] * dir[1] + dir[2] * dir[2] + dir[3] * dir[3])
	if length >= 0.25 then
		dir = { dir[1] / length, dir[2] / length, dir[3] / length }
	else
		dir = { cast.facing[1], cast.facing[2], cast.facing[3] }
		local f = math.sqrt(dir[1] * dir[1] + dir[2] * dir[2] + dir[3] * dir[3])
		if f > 0.001 then
			dir = { dir[1] / f, dir[2] / f, dir[3] / f }
		else
			dir = { 1, 0, 0 }
		end
	end
	local start = { target[1], target[2], target[3] }
	local facing = { dir[1], dir[2], dir[3] }
	if at_point and cast.type == kLightning then
		-- Height is lane 2 (the targeted classes compare heights on it).
		start[3] = target[3] + 15
		facing = { 0, 0, -1 }
	elseif cast.type == kForcePush then
		for i = 1, 3 do
			if length >= 0.25 then start[i] = origin[i] else start[i] = target[i] - dir[i] end
		end
	end
	casting = { type = cast.type, hero = hero }
	local spell, serial = Refii.cast_spell(hero, cast.type, true, cast.victim, cast.level, start, facing)
	casting = nil
	NoteWeaveSerial(serial)
	if not at_point then
		Refii.log("Spell Weaving: partner " .. cast.type .. " cast on " .. cast.victim .. " at power " .. (cast.level + 1) ..
			" for player " .. cast.player .. " (spell " .. tostring(spell) .. " serial " .. tostring(serial) .. ")")
	else
		Refii.log("Spell Weaving: partner " .. cast.type .. " cast at (" .. cast.where[1] .. ", " .. cast.where[2] .. ", " ..
			cast.where[3] .. ") at power " .. (cast.level + 1) .. " for player " .. cast.player .. " (spell " .. tostring(spell) ..
			" serial " .. tostring(serial) .. ")")
	end
end

-- Queues the partner on a victim. One pending cast per victim and type: a second request
-- refreshes it, as Fable 3's delayed caster reuses the slot of the same spell type. Counted on
-- every request, refreshed or new: the fireball's "struck nothing alive" test asks whether its
-- blast asked for any partner at all.
local function QueuePartner(p, t, level, victim, delay, origin)
	queued = queued + 1
	for i = 1, #pending do
		local c = pending[i]
		if c.player == p and c.type == t and c.victim == victim then
			c.level = level
			return
		end
	end
	if #pending >= kPending then return end
	pending[#pending + 1] = {
		player = p, type = t, level = level, victim = victim, at = clock + delay,
		where = { 0, 0, 0 }, facing = { 0, 0, 1 }, origin = { origin[1], origin[2], origin[3] },
	}
end

-- Queues the partner at a point with no victim — each impact is its own cast.
local function QueuePartnerAt(p, t, level, where, facing, delay)
	if #pending >= kPending then return end
	queued = queued + 1
	pending[#pending + 1] = {
		player = p, type = t, level = level, victim = 0, at = clock + delay,
		where = { where[1], where[2], where[3] }, facing = { facing[1], facing[2], facing[3] },
		origin = { where[1], where[2], where[3] },
	}
end

----------------------------------------------------------------------------------------------------
-- THE LOOK, PLAYED PER CAST. Each pair has its own mod effect, fx/effects/spell_weaving_<base>_
-- <overlay>.json, built from the particle bank's own entities so both looks draw it. A weave plays
-- it with Refii.play_fx_on on each thing its base put in the world — the vortex entity, the
-- fireball's shot, each live sword, each victim of a bolt, shove or chaos for its hold — and
-- Refii.stop_fx ends each play when that thing is gone; with nothing to ride (a fireball's blast,
-- an instant cast that hit nobody) it plays once at the point. fx/recipes.json tints each base
-- effect by the play bound to it ("with"), not by a table.
----------------------------------------------------------------------------------------------------

local kLookNames = { [0] = "shock", [1] = "fire", [3] = "blades", [4] = "vortex", [5] = "chaos", [6] = "push" }

-- The pair's own effect; nil for a pair with none (a spell woven with itself under allow_same).
local function LookName(base, overlay)
	if base == overlay or kLookNames[base] == nil or kLookNames[overlay] == nil then return nil end
	return "spell_weaving_" .. kLookNames[base] .. "_" .. kLookNames[overlay]
end

-- Whether a base's look is one burst per rider rather than a look that lasts: an instant base (a
-- bolt, a shove) casts its partner once per victim, and its pair's look is Fable III's half-second
-- burst (fxspl_lightning_push), so a repeat over the victim's hold would pulse.
local function PlaysOnce(base)
	return base == kLightning or base == kForcePush
end

-- EACH SWORD WEARS ITS PARTNER'S TINT, through the game's own appearance tint setter
-- (Refii.entity_tint), so the entity feed draws it under both looks and every machine running the
-- cast tints its own copy. The sword's update restates its scale and alpha every frame and never
-- its tint, so it is set once and held; a sword dies 0.35 s after its stab, and nothing is put
-- back. The colours multiply the golden blade's lit colour: a lane above 1 is what lets blue and
-- purple show on gold; Force Push keeps no glow, a dark slate steel. settings.json overrides these.
local kSwordTintDefaults = {
	[kFireball] = { "fire", 1.35, 0.24, 0.1 },
	[kLightning] = { "shock", 0.35, 0.75, 3.0 },
	[kChaos] = { "chaos", 0.5, 0.22, 3.0 },
	[kForcePush] = { "push", 0.28, 0.32, 0.55 },
}

local function SwordTint(overlay)
	local d = kSwordTintDefaults[overlay]
	if d == nil then return nil end
	return Knob("sword_tint_" .. d[1] .. "_r", d[2]), Knob("sword_tint_" .. d[1] .. "_g", d[3]),
		Knob("sword_tint_" .. d[1] .. "_b", d[4])
end

-- Tints each live sword of a Blades weave by its partner, once.
local function TintSwords(w)
	if w.base ~= kSwords then return end
	local swords = LiveSwords(w)
	local r, g, b
	for i = 1, #swords do
		local sword = swords[i]
		if not w.sword_tinted[sword] then
			if r == nil then
				r, g, b = SwordTint(w.overlay)
				if r == nil then return end
			end
			if Refii.entity_tint(sword, r, g, b, 1) then w.sword_tinted[sword] = true end
		end
	end
end

-- The Blades' swords of a cast, as its Start stated them inside the factory (spell_parts).
local function ReadSwords(w, parts)
	w.swords_read = true
	w.swords = (parts and parts.swords) or {}
	Refii.log("Spell Weaving: blades cast serial " .. w.serial .. " holds " .. #w.swords .. " sword(s)")
end

-- What a weave's look rides this tick: entities (each its own play), or — with none, while the
-- base is out — the point at the weave's origin (0). Each sword, each victim of a bolt, shove or
-- chaos within its hold, else the anchor (the vortex, the shot in flight), else the origin (an
-- instant cast that hit nobody). A base's end (the vortex's shutdown, the fireball's blast) ends
-- its look: a play restarted at the end point would pop for its short hold.
local function Riders(w, live)
	local out = {}
	if w.base == kSwords then
		local swords = LiveSwords(w)
		for i = 1, #swords do
			if #out < kMaxLooks then out[#out + 1] = swords[i] end
		end
		return out
	end
	if w.base == kLightning or w.base == kForcePush or w.base == kChaos then
		local hold = HoldSeconds(w.base)
		for i = 1, #w.victims do
			local v = w.victims[i]
			if #out < kMaxLooks and clock - v.at <= hold and Refii.alive(v.e) then out[#out + 1] = v.e end
		end
		if #out > 0 then return out end
	end
	if w.anchor ~= 0 or live then out[1] = w.anchor end  -- 0: the point
	return out
end

-- Each live weave plays its look on what it rides, and stops each play whose rider has gone.
local function UpdateLooks()
	for n = 1, #weaves do
		local w = weaves[n]
		local live = BaseLive(w)
		local holding = not live and w.done_at >= 0 and clock - w.done_at <= HoldSeconds(w.base)
		local look = LookName(w.base, w.overlay)
		local riders = {}
		if (live or holding) and look ~= nil then riders = Riders(w, live) end

		local kept = {}
		for i = 1, #w.looks do
			local play = w.looks[i]
			local wanted = false
			for r = 1, #riders do
				if riders[r] == play.on then wanted = true end
			end
			if wanted then
				kept[#kept + 1] = play
			else
				Refii.stop_fx(play.id)
			end
		end
		w.looks = kept

		for r = 1, #riders do
			local playing = false
			for i = 1, #w.looks do
				if w.looks[i].on == riders[r] then playing = true end
			end
			if not playing and #w.looks < kMaxLooks then
				local id
				if riders[r] ~= 0 then
					id = Refii.play_fx_on(riders[r], look, { loop = not PlaysOnce(w.base) })
				else
					-- At a point: bounded, so a play whose stop never comes (the mod unmounted) still ends.
					id = Refii.play_fx(look, w.origin[1], w.origin[2], w.origin[3],
						{ loop = not PlaysOnce(w.base), duration = HoldSeconds(w.base) + 8.0 })
				end
				if id ~= nil and id ~= 0 then w.looks[#w.looks + 1] = { on = riders[r], id = id } end
			end
		end
	end
end

----------------------------------------------------------------------------------------------------
-- THE LAUNCH
----------------------------------------------------------------------------------------------------

Refii.on("spell_launch", function(e)
	local p = e.player
	if not IsPlayer(p) or e.slot < 0 or e.slot >= kSlots then return end

	-- Fable 3 pacing: one cast at the charged power. The cascade's lower slots are skipped; the
	-- cascade itself runs on and reaches the top slot regardless of what the launch did.
	if e.cascading and Knob("top_only", 1) ~= 0 then return true end

	local primary = e.type
	local partner = LearnedPartner(p, e.caster, e.slot)
	local weave = Knob("woven", 1) ~= 0 and e.slot + 1 <= Level(p) and Weavable(primary) and partner >= 0 and
		(primary ~= partner or Knob("allow_same", 0) ~= 0)
	if not weave then return end

	local top = primary
	if kRank[primary] > kRank[partner] then top = partner end
	local under = primary
	if top == primary then under = partner end
	-- The base is the one cast; the overlay is never launched here — spell_created binds the pair
	-- to the spell this launch creates and the base's hits cast the overlay on each victim.
	ArmWeave(p, top, under, e.slot)
	e.type = top
	Refii.log("Spell Weaving: cast " .. top .. " carrying " .. under .. " at power " .. (e.slot + 1) .. " for player " .. p ..
		" (slot held " .. primary .. ")")
end)

-- Inside the factory, before spell_created: the armed vortex cast of this caster takes it. Never
-- this mod's own partner vortex (its serial is noted before its deferred events arrive).
Refii.on("vortex_started", function(e)
	local a = armed[e.caster_player]
	if e.entity ~= 0 and a and a.base == kVortex and not IsWeaveSerial(e.serial) then
		a.vortex_serial = e.serial
		a.vortex_entity = e.entity
	end
end)

-- The armed pair becomes a weave: on the launch's spell object here, or already on its first hit
-- when that landed inside the factory (hit_landed), in which case this completes the record. Only
-- an armed player's base of the armed type binds; this mod's own partner casts come here too
-- (deferred, after the chunk that cast them) and are known by their serial, so only a base binds.
Refii.on("spell_created", function(e)
	local p = e.caster_player
	local a = armed[p]
	if not IsPlayer(p) or a == nil or e.type ~= a.base or IsWeaveSerial(e.serial) then return end
	armed[p] = nil
	local w = WeaveBySerial(e.serial)
	if w == nil then w = AddWeave(e.serial, p, a.base, a.overlay) end
	w.spell = e.spell
	if e.level >= 0 and e.level < kLevels then w.level = e.level else w.level = a.level end
	-- The vortex stated inside this factory call carries this cast's serial: it is this cast's.
	w.vortex_live = a.vortex_serial ~= nil and a.vortex_serial == e.serial
	local x, y, z = Refii.position(e.caster)
	if x ~= nil then w.origin = { x, y, z } end
	if w.base == kVortex and w.vortex_live then
		if a.vortex_entity ~= 0 and Refii.position(a.vortex_entity) ~= nil then w.anchor = a.vortex_entity end
	elseif w.base == kFireball then
		local parts = Refii.spell_parts(e.serial)
		local shot = parts and parts.anchor
		if shot and Refii.position(shot) ~= nil then w.anchor = shot end
	elseif w.base == kSwords then
		-- The swords are there: the factory ran the class's Start.
		ReadSwords(w, Refii.spell_parts(e.serial))
		TintSwords(w)
	elseif (w.base == kLightning or w.base == kForcePush or w.base == kChaos) and e.targeted and e.target ~= 0 and
		not Refii.world_here() then
		-- Where hits are not decided (an online joiner) the victims arrive as the host's relayed
		-- hits, a network moment later: until then the look rides on the target the cast states.
		NoteVictim(w, e.target)
	end
	Refii.log("Spell Weaving: weave serial " .. w.serial .. " = " .. w.base .. " carrying " .. w.overlay .. " at power " ..
		(w.level + 1) .. " for player " .. p .. " (anchor " .. w.anchor .. ", vortex " .. tostring(w.vortex_live) .. ")")
end)

----------------------------------------------------------------------------------------------------
-- THE HITS
----------------------------------------------------------------------------------------------------

-- A weave-side spell's hit lands at the weave's share: Fable 3's GeneralDamageMultiplier. A hit
-- landing inside the partner's own factory call (a Lightning bolt) arrives live, nested in the
-- cast, carrying a serial nobody has seen yet: it is the cast in progress when its type and
-- caster match, and its serial is noted here.
Refii.on("hit", function(e)
	if Knob("woven", 1) == 0 then return end
	if casting ~= nil then
		if e.spell_type ~= casting.type or e.attacker ~= casting.hero or e.spell_serial == nil or e.spell_serial == 0 then
			return
		end
		NoteWeaveSerial(e.spell_serial)
	elseif not IsWeaveSerial(e.spell_serial) then
		return
	end
	e.damage = e.damage * Knob("weave_damage", 0.7)
end)

-- The weave a hit relayed from the host belongs to: by serial when this machine's copy of the cast
-- carries the host's number, else the newest weave of the attacker's player whose base is the
-- hit's spell. Nil when none is out.
local function WeaveForReplica(e)
	local w = WeaveBySerial(e.spell_serial)
	if w then return w end
	local newest = nil
	for i = 1, #weaves do
		local c = weaves[i]
		if c.player == e.attacker_player and c.base == e.spell_type and (newest == nil or c.age < newest.age) then
			newest = c
		end
	end
	return newest
end

-- The hit landed first, as the game delivers it; the partner's cast follows it — the partner of
-- the WEAVE whose serial the hit carries. CO-OP: hits are decided where the world runs, so partners
-- are cast where Refii.world_here holds (the host, or the one machine alone and in local co-op); a
-- relayed hit (an online joiner) only notes its victim for the look.
Refii.on("hit_landed", function(e)
	if Knob("woven", 1) == 0 or e.spell_type < 0 or IsWeaveSerial(e.spell_serial) then
		return  -- a weave-side spell never casts a partner of its own
	end
	if Refii.event_replica() then
		local w = WeaveForReplica(e)
		if w and e.spell_type == w.base then NoteVictim(w, e.victim) end
		return
	end
	local w = WeaveBySerial(e.spell_serial)
	if w == nil then
		-- A hit inside the factory, before spell_created (a targeted Lightning's bolt lands from
		-- the class's Start): the armed base of this caster binds here and spell_created
		-- completes the record (the spell object, its anchor) by serial.
		local p = e.attacker_player
		local a = armed[p]
		if not IsPlayer(p) or a == nil or e.spell_type ~= a.base or e.spell_serial == 0 then return end
		w = AddWeave(e.spell_serial, p, a.base, a.overlay)
		if e.spell_level >= 0 and e.spell_level < kLevels then w.level = e.spell_level else w.level = a.level end
		local x, y, z = Refii.position(e.attacker)
		if x ~= nil then w.origin = { x, y, z } end
		Refii.log("Spell Weaving: weave serial " .. w.serial .. " = " .. w.base .. " carrying " .. w.overlay ..
			" bound on its first hit for player " .. p)
	end
	if e.spell_type ~= w.base or w.overlay < 0 then return end
	NoteVictim(w, e.victim)
	if not Refii.alive(e.victim) or e.victim == e.attacker or not Fresh(e.victim, e.spell_serial, w.overlay) then return end
	local level = w.level
	if e.spell_level >= 0 and e.spell_level < kLevels then level = e.spell_level end
	-- Fable III: WeaveSpellShotDelayTime, 0.25 s in the vortex records and 1 s by default, ±5 %.
	local delay
	if w.base == kVortex then delay = Knob("shot_delay_vortex", 0.25) else delay = Knob("shot_delay", 1.0) end
	delay = delay * Jitter()
	if delay < 0 then delay = 0 end
	-- Where the base stands: the push is away from here.
	local origin = { w.origin[1], w.origin[2], w.origin[3] }
	if w.base == kVortex and w.vortex_live then
		-- The twister's point, as spell_parts states it for a live vortex.
		local parts = Refii.spell_parts(w.serial)
		if parts and parts.origin then origin = { parts.origin[1], parts.origin[2], parts.origin[3] } end
	elseif w.anchor ~= 0 and Refii.alive(w.anchor) then
		local x, y, z = Refii.position(w.anchor)
		if x ~= nil then origin = { x, y, z } end
	elseif w.base == kSwords or w.base == kLightning then
		-- A stab or a bolt: the base stands where the hit landed; push away from the hero's side.
		local x, y, z = Refii.position(e.attacker)
		if x ~= nil then origin = { x, y, z } end
	end
	QueuePartner(w.player, w.overlay, level, e.victim, delay, origin)
end)

-- The look holds for its fade where the twister ended, not where the cast began.
Refii.on("vortex_ended", function(e)
	local w = WeaveBySerial(e.serial)
	if w and w.base == kVortex then
		w.origin = { e.x, e.y, e.z }
		w.vortex_live = false
		w.anchor = 0
	end
end)

Refii.on("fireball_impact", function(e)
	local w = WeaveBySerial(e.serial)
	if w == nil then return end
	-- The blast's hits landed INSIDE the impact, each queuing the partner on its victim: the
	-- impact point is the origin from here on, and the shot entity — which keeps falling after —
	-- is no anchor.
	w.origin = { e.x, e.y, e.z }
	w.anchor = 0
	if not e.detonated then return end
	-- The detonation, once. A detonation whose blast delivered no hit struck nothing alive, and
	-- the partner goes to the point — on the machine that runs the world only: an online joiner's
	-- blast delivers no hits, and the host's copy queues for it.
	w.done_at = clock  -- the look holds on the blast for its fade; the projectile is gone
	if e.hits ~= 0 or Knob("woven", 1) == 0 or w.overlay < 0 or not Refii.world_here() then return end
	local level = w.level
	if e.level >= 0 and e.level < kLevels then level = e.level end
	local delay = Knob("shot_delay", 1.0) * Jitter()
	if delay < 0 then delay = 0 end
	QueuePartnerAt(w.player, w.overlay, level, { e.x, e.y, e.z }, { e.nx, e.ny, e.nz }, delay)
	Refii.log("Spell Weaving: fireball serial " .. w.serial .. " struck nothing alive at (" .. e.x .. ", " .. e.y .. ", " .. e.z ..
		"); partner " .. w.overlay .. " queued there")
end)

----------------------------------------------------------------------------------------------------
-- THE TICK (every guest tick, e.seconds that tick's)
----------------------------------------------------------------------------------------------------

Refii.on("tick", function(e)
	clock = clock + e.seconds

	if Refii.hero(1) ~= nil or Refii.hero(2) ~= nil then  -- a session: a hero exists
		if reread then
			reread = false
			ReadSaved()
		end
		PublishActive()
		PublishHudPartner(e.seconds)
	end

	-- The partner casts whose delay has run out.
	local waiting = {}
	for i = 1, #pending do
		local c = pending[i]
		if clock >= c.at then CastPartner(c) else waiting[#waiting + 1] = c end
	end
	pending = waiting

	-- The weaves age; a finished base's record holds for its fade, then frees.
	local n = 1
	while n <= #weaves do
		local w = weaves[n]
		w.age = w.age + e.seconds
		if not BaseLive(w) and w.done_at < 0 then w.done_at = clock end
		if (w.done_at >= 0 and clock - w.done_at > HoldSeconds(w.base) + 1.0) or w.age > 60.0 or Refii.hero(w.player) == nil then
			Refii.log("Spell Weaving: weave serial " .. w.serial .. " over")
			RemoveWeave(n)
		else
			n = n + 1
		end
	end

	-- An arm is consumed inside the launch that set it (the factory runs within the launch); one
	-- that reaches a tick belonged to a launch that made no spell (no will, no target) and must
	-- not bind the next plain cast.
	armed[1] = nil
	armed[2] = nil

	for i = 1, #weaves do TintSwords(weaves[i]) end
	UpdateLooks()
end)

W.Weavable = Weavable
W.Rank = kRank
W.HoldSeconds = HoldSeconds
W.LookName = LookName
