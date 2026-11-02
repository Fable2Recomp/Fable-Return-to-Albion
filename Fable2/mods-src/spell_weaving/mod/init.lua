-- Spell Weaving — two spells from one release, Fable 3's gauntlets in Fable 2. Pure Lua on the
-- mod API (Godot/docs/modding/): no native code, no raw guest read, no hook of its own.
--
-- THE PAIR. Of the slot's spell and its partner, the higher in the hierarchy is the BASE and is
-- the one spell launched: `spell_launch` carries the slot's type writable for that launch, and
-- when the slot holds the lower one the type becomes the base's, so position, target, targeted /
-- area and the launch notification (where Souls Combat charges mana) are all the game's. The lower
-- spell, the OVERLAY, is never launched; it rides the base's hits:
--   - a Force Push overlay on Shock, Fireball or Blades is the base's own hits' knockdown (`hit`'s
--     writable `knockdown`, the byte Force Push's own hit sets): no push is cast, no push look plays
--     (Shock + Force Push overlays Force Push's own hit effect on each victim of the bolt);
--   - a Vortex base lifts only for Blades: Fable III's rule, one Blades cast per creature it reaches
--     (never a prop, a sword or another spell's entity it lifts) — on one it holds at a moment its
--     roll picks in the twister's life, while it still holds it; on one it cannot lift at its
--     shudder hit — the swords Fable III's weave makes. With Fire, Shock, Chaos or Force Push it lifts
--     nobody (`vortex_started`'s writable `lifts`) and passes through: the one shudder hit it gives
--     each creature it reaches lands the vortex's own hit damage (`hit`), Fire and Shock that plus
--     `weave_damage` of it and the burn or the shock, Chaos the chaos, Force Push a shove away from
--     the twister;
--   - every other pair casts the overlay on each victim the base hits (`hit_landed`): a real
--     Targeted cast placed by the spell's own rule (cast_spell's `at`), a short delay later, as
--     Fable III's primary issues its second cast, its hits at `weave_damage` and its status at
--     `weave_duration` of their own (Fable III's WeaveSpellMultipliers).
-- A cast weaves only up to the Spell Weaving level: level N weaves power N and below.
--
-- A CAST IS KNOWN BY ITS TAG. The launch writes `spell_launch`'s tag, and every event of the cast
-- carries it — spell_created, vortex_started, vortex_caught, summoned, hit, hit_landed,
-- fireball_impact, spell_ended — a hit landed inside the factory included. A partner cast carries
-- its base's tag negated, so its hits and its status (`status_applying`) are the weave side
-- wherever they arrive.
--
-- EACH PLAYER'S CHOICES LIVE IN THE PLAYER STORE, SAVED with the hero, the same on both co-op
-- machines:
--   spell_weaving.ability        the Spell Weaving level, 0..5: the ENGINE buys and unlearns it
--                                from ui/abilities.json (price, refund, saves from before)
--   spell_weaving.partner_1..5   the partner picked for each slot, -1 for none: the ENGINE's spell
--                                pick of ui/spellwheel.json writes it, refuses Slow Time and Raise
--                                Dead on every path (`exclude`) and takes the choice a save from
--                                before kept in its Lua table (`legacySaveKey` / `legacySaveKeyTwo`)
-- and, stated where the hero is played (session values): spell_weaving.partner_active_1..5 (what
-- is in effect per slot: chosen, learned, within the level) and spell_weaving.hud_partner (the
-- glyph while charging, ui/hud.json).
--
-- Settings (settings.json, Refii.setting):
--   spell_weaving.top_only       1: only the charged slot casts (Fable 3); 0: Fable 2's cascade
--   spell_weaving.allow_same     1: a partner equal to the slot's spell casts it twice
--   spell_weaving.woven          1: weaving on; 0: every slot casts its own spell alone
--   spell_weaving.woven_cooldown_seconds, shot_delay, shot_delay_vortex, weave_damage,
--   spell_weaving.weave_duration, vortex_affect_probability, vortex_window_start, vortex_window_end,
--   spell_weaving.vortex_delay_cap, vortex_swords_1..5,
--   spell_weaving.sword_tint_<fire|shock|chaos|push>_<r|g|b>
--
-- Handles, not addresses: every entity and spell object here is the handle the events hand over;
-- a serial or a tag is compared, never computed from the game's state.

SpellWeaving = SpellWeaving or {}
local W = SpellWeaving

local function Knob(key, default)
	return Refii.setting("spell_weaving." .. key, default)
end

local kMaxLevel = 5
local kSlots = 5
local kLevels = 5

local kLightning = Refii.SPELL_LIGHTNING
local kFireball = Refii.SPELL_FIREBALL
local kSlowTime = Refii.SPELL_SLOW_TIME
local kSwords = Refii.SPELL_BLADES
local kVortex = Refii.SPELL_VORTEX
local kChaos = Refii.SPELL_CHAOS
local kForcePush = Refii.SPELL_FORCE_PUSH
local kDeadRising = Refii.SPELL_RAISE_DEAD

-- status_effect / apply_status / has_status's kinds; summoned's Blades sword.
local kStatusElectrified = 1
local kStatusChaos = 2
local kStatusBurning = 3
local kSummonSword = 2

-- THE HIERARCHY: Vortex > Blades > Fireball > Shock > Force Push > Chaos — Fable 3's own primaries
-- (the vortex casts its partner per creature it holds, a blade per stab, a fireball on impact). Of
-- a woven pair the higher spell is the BASE, the one cast; the lower is the OVERLAY, riding the
-- base's hits and applied onto the base's look. Lower rank is higher.
local kRank = {
	[kLightning] = 3, [kFireball] = 2, [kSlowTime] = 99, [kSwords] = 1,
	[kVortex] = 0, [kChaos] = 5, [kForcePush] = 4, [kDeadRising] = 99,
}

local function Weavable(t)
	return type(t) == "number" and t >= kLightning and t <= kDeadRising and t ~= kSlowTime and t ~= kDeadRising
end

local function IsPlayer(p)
	return p == 1 or p == 2
end

-- A VORTEX THAT PASSES THROUGH: the overlays a vortex base lifts nobody for, each with the status
-- its pass-through hit puts on (0 for Force Push, whose hit shoves instead; its time is the
-- overlay's own, Refii.spell_balance) and whether that hit lands the overlay's extra damage.
local kVortexPass = {
	[kFireball] = { status = kStatusBurning, extra = true },
	[kLightning] = { status = kStatusElectrified, extra = true },
	[kChaos] = { status = kStatusChaos, extra = false },
	[kForcePush] = { status = 0, extra = false },
}

-- Fable III's Vortex + Swords: the swords its weave cast makes at each power, the NumSwords of
-- VortexTargeted/WeaveSpellData/WEAVE_SPELL_VORTEX_SWORDS/WeaveSpellData/Power0..4/BalanceData.
local kVortexSwords = { 3, 4, 4, 4, 5 }

-- Whether a pair's overlay is the base's own hits' knockdown rather than a cast: Force Push under
-- Shock, Fireball or Blades.
local function KnockdownOverlay(base, overlay)
	return overlay == kForcePush and (base == kLightning or base == kFireball or base == kSwords)
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

-- The slot's partner if the player's hero can assign it (Refii.spells_known), else -1.
local function LearnedPartner(p, slot)
	local t = Partner(p, slot)
	if not Weavable(t) then return -1 end
	local known = Refii.spells_known(p)
	if known == nil then return -1 end
	for i = 1, #known do
		if known[i] == t then return t end
	end
	return -1
end

-- What is in effect per slot (chosen, learned, and the slot within the Spell Weaving level),
-- stated where the hero is played: the selectors draw the chosen pick faint when it is not this.
local function PublishActive(p)
	if Refii.hero(p) == nil or not Refii.player_here(p) then return end
	local level = Level(p)
	for slot = 0, kSlots - 1 do
		local active = -1
		if slot < level then active = LearnedPartner(p, slot) end
		Refii.player_set(p, ActiveKey(slot), active)
	end
end

-- Restated on the tick after anything it reads changes: the purchase (mod_ability), a spell's
-- rank (ability_level) and the pick (spell_pick) are each raised before their write; the hero
-- arriving, a save load and a Lua rebuild replace the store, and the engine takes a save from
-- before's picks on the hero's first tick.
local function Restate(p)
	if not IsPlayer(p) then return end
	Refii.after(0, function() PublishActive(p) end, "spell_weaving.active_" .. p)
end

local function RestateBoth()
	Restate(1)
	Restate(2)
end

Refii.on("hero_ready", function(e) Restate(e.player) end)
Refii.on("mod_ability", function(e) Restate(e.player) end)
Refii.on("ability_level", function(e) Restate(e.player) end)
Refii.on("spell_pick", function(e)
	if e.pick == "spell_weaving.partner" then Restate(e.player) end
end)
Refii.on("save_loaded", function() RestateBoth() end)
Refii.on("lua_ready", function() RestateBoth() end)
RestateBoth()

----------------------------------------------------------------------------------------------------
-- THE HUD GLYPH (ui/hud.json): while a hero charges, the partner the charged slot would weave is
-- stated in that player's store as spell_weaving.hud_partner, -1 when none. The charge level (the
-- top slot, 0..4) arrives every frame of the charge (spell_charge_level) and is restated when it
-- moves; the charge's end (spell_charge_ended, released into a cast or not) clears it. The glyph
-- shows what the launch would weave: the slot's own spell must be weavable and differ from the
-- partner (allow_same aside), read from the slot table as the launch reads it.
----------------------------------------------------------------------------------------------------

local hud_slot = { [1] = -1, [2] = -1 }

local function HudPartner(p, slot)
	if slot < 0 or slot >= kSlots or slot >= Level(p) or Knob("woven", 1) == 0 then return -1 end
	local primary = Refii.spell_in_slot(p, slot)
	local partner = LearnedPartner(p, slot)
	if not Weavable(primary) or (primary == partner and Knob("allow_same", 0) == 0) then return -1 end
	return partner
end

Refii.on("spell_charge_level", function(e)
	local p = e.player
	if not IsPlayer(p) or e.level == hud_slot[p] or not Refii.player_here(p) then return end
	hud_slot[p] = e.level
	Refii.player_set(p, "spell_weaving.hud_partner", HudPartner(p, e.level))
end)

Refii.on("spell_charge_ended", function(e)
	local p = e.player
	if not IsPlayer(p) then return end
	hud_slot[p] = -1
	Refii.player_set(p, "spell_weaving.hud_partner", -1)
end)

----------------------------------------------------------------------------------------------------
-- ONE WEAVE PER CAST. Every woven release is its own record from its launch until its base has
-- finished: the tag the launch wrote, the base's serial (noted from the first event of the cast),
-- the pair, the power, and what the look rides on — the vortex entity, the fireball's shot, each
-- of the Blades' swords. Two vortices from two slots keep two partners; a slot re-picked between
-- casts changes nothing already out.
----------------------------------------------------------------------------------------------------

local kMaxWeaves = 8
local kMaxLooks = 10
local kMaxVictims = 8
local kMaxSwords = 8

-- The tags this mod writes: a base cast's in [kTagFirst, kTagFirst + kTagSpan), its partner casts'
-- that tag negated; exact in the game Lua's 32-bit float (below 2^24). The counter lives in the
-- mod's table, so a Lua rebuild never re-issues the tag of a cast still in the world. Online, the
-- machine not deciding a cast takes the decider's tag into its own copy of the spell
-- (Godot/docs/modding/gameplay.md, "One cast, one identity online"), so the two machines write
-- from the two halves of the range — the world's machine the lower, a joiner the upper — and a tag
-- taken from the peer never names a weave this machine launched under its own.
local kTagFirst = 6160384
local kTagSpan = 65536
local kTagHalf = kTagSpan / 2
W.tag_next = W.tag_next or 0

local function NextTag()
	W.tag_next = (W.tag_next + 1) % kTagHalf
	if Refii.world_here() then return kTagFirst + W.tag_next end
	return kTagFirst + kTagHalf + W.tag_next
end

local function IsBaseTag(tag)
	return type(tag) == "number" and tag >= kTagFirst and tag < kTagFirst + kTagSpan
end

local function IsPartnerTag(tag)
	return type(tag) == "number" and IsBaseTag(-tag)
end

local weaves = {}

local function NewWeave(tag, p, base, overlay, level)
	return {
		tag = tag, adopted = 0, serial = 0, player = p, slot = level, base = base, overlay = overlay, level = level,
		anchor = 0, vortex_live = false, vortex_damage = 0, swords = {}, sword_tinted = {}, origin = { 0, 0, 0 },
		victims = {}, born = Refii.now(), ended = false, done_at = -1, looks = {},
		vortex_born = 0, vortex_duration = 0, partnered = {}, waiting = {}, blades_level = -1,
	}
end

-- By the tag this machine's launch wrote, or the decider's tag its copy of the cast took.
local function WeaveByTag(tag)
	if not IsBaseTag(tag) then return nil end
	for i = 1, #weaves do
		if weaves[i].tag == tag or weaves[i].adopted == tag then return weaves[i] end
	end
	return nil
end

local function WeaveBySerial(serial)
	if serial == nil or serial == 0 then return nil end
	for i = 1, #weaves do
		if weaves[i].serial == serial then return weaves[i] end
	end
	return nil
end

-- The weave an event of a cast belongs to: by the tag it carries, else by this machine's serial of
-- the cast. The first event of the cast states its serial; an event carrying the local serial with
-- the decider's tag (online, once the core paired the two copies) states that tag as the weave's.
-- `serial` is always this machine's: a relayed hit is rewritten to it by the core (hit_landed).
local function WeaveOf(tag, serial)
	local valid = serial ~= nil and serial ~= 0 and serial ~= 4294967295
	local w = WeaveByTag(tag)
	if w == nil and valid then w = WeaveBySerial(serial) end
	if w == nil then return nil end
	if w.serial == 0 and valid then w.serial = serial end
	if IsBaseTag(tag) and tag ~= w.tag then w.adopted = tag end
	return w
end

-- How long a base's look holds on a victim and after the base finished: the burst a bolt or a
-- shove plays, the fireball's blast.
local kHoldSeconds = { [kLightning] = 1.5, [kForcePush] = 1.5, [kFireball] = 1.5 }

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
	table.remove(weaves, index)
end

-- A record for a new weave: a free one, else the oldest weave's, its looks stopped.
local function AddWeave(tag, p, base, overlay, level)
	if #weaves >= kMaxWeaves then
		local oldest = 1
		for i = 2, #weaves do
			if weaves[i].born < weaves[oldest].born then oldest = i end
		end
		RemoveWeave(oldest)
	end
	local w = NewWeave(tag, p, base, overlay, level)
	weaves[#weaves + 1] = w
	return w
end

-- SHOCK + FORCE PUSH: the bolt's victim wears Force Push's own hit effect over the bolt's, once
-- per victim per cast, on every machine that sees the hit; no push is cast.
local kPushHitFx = "fxspl_forcepush_hit"

-- A victim the base hit, kept (newest over oldest) for the looks that ride on victims.
local function NoteVictim(w, victim)
	if victim == nil or victim == 0 then return end
	local now = Refii.now()
	local slot = 1
	for i = 1, #w.victims do
		if w.victims[i].e == victim then
			w.victims[i].at = now
			return
		end
		if w.victims[i].at < w.victims[slot].at then slot = i end
	end
	if #w.victims < kMaxVictims then slot = #w.victims + 1 end
	w.victims[slot] = { e = victim, at = now }
	if w.base == kLightning and w.overlay == kForcePush then
		Refii.play_fx_on(victim, kPushHitFx, { loop = false })
	end
end

-- Whether the base still holds a victim: a chaos base while the victim is turned (the game's own
-- status), a bolt or a shove for its burst's hold.
local function Holding(w, v, now)
	if w.base == kChaos then return Refii.has_status(v.e, kStatusChaos) end
	return now - v.at <= HoldSeconds(w.base)
end

-- Whether the weave's base is still out: the vortex live, a sword hovering, the fireball not yet
-- detonated, an instant cast holding a victim; else until the registry ends the spell.
local function BaseLive(w)
	if w.base == kVortex then
		return w.vortex_live
	elseif w.base == kSwords then
		return #w.swords > 0 or not w.ended
	elseif w.base == kFireball then
		return w.done_at < 0 and not w.ended
	end
	local now = Refii.now()
	for i = 1, #w.victims do
		if Holding(w, w.victims[i], now) then return true end
	end
	return not w.ended
end

----------------------------------------------------------------------------------------------------
-- THE PARTNER CAST. Once per victim per base cast, never twice within the cooldown; delayed for
-- Fable III's shot delay and cast by the game's own factory with the hero as caster.
----------------------------------------------------------------------------------------------------

-- One partner per victim per base cast, and no second of one type on a victim within the
-- cooldown: kept on the victim (Refii.entity_value, forgotten when it leaves the world).
local function Fresh(victim, tag, t)
	local key = "spell_weaving.woven_" .. t
	local last = Refii.entity_value(victim, key)
	local now = Refii.now()
	if last ~= nil and (last.tag == tag or last.until_ > now) then return false end
	Refii.entity_set(victim, key, { tag = tag, until_ = now + Knob("woven_cooldown_seconds", 0.5) })
	return true
end

-- Fable 3: value * (1 + 0.1 * (rand - 0.5)) — within five per cent either way.
local function Jitter()
	return 1 + 0.1 * (math.random() - 0.5)
end

-- THE VORTEX'S HITS. Each reaches the one receiver (rex_CECHitReceiver_OnHit, TU1 sub_825C5220),
-- so `hit` and `hit_landed`, and all but the drop carry the cast's serial, so its tag (type 4 at
-- hit +0x54, the serial at +0x44) — Fable 2 TU1:
--   - the held hit, on a creature it holds that it may lift: sub_8275D8B0 (`bl sub_8275D8B0`
--     0x8275bdf8 in the walk of the held list +0x1B0), only when the classifier answers 1 (`cmpwi
--     cr6,r11,1; bne` 0x8275d924..28), serial `lwz r28,0x18(r25)` 0x8275d950 into the hit (`stw
--     r27,0x44(r31)` 0x825fb7a0 in sub_825FB518), delivered `bl sub_825C5220` 0x825fbc5c;
--   - the ragdoll hit, on a held entity: sub_8275BF68 (0x8275bc80 in the same walk; 0x8275ca04 in
--     the capture, after the entry's append `bl sub_8236AC38` 0x8275b964), serial `lwz
--     r28,0x18(r22)` 0x8275c510 as sub_825FA078's r5 (`stw r26,0x1D0+var_12C(r1)` 0x825fa43c, hit
--     +0x44), delivered `bl sub_825C5220` 0x825fa44c;
--   - the shudder, once per cast on a creature it cannot lift: `cmpwi cr6,r11,2` 0x8275b008, serial
--     `lwz r14,0x18(r22)` 0x8275b270 / 0x8275b334 into sub_825FA488 (→ sub_825FA680, `stw
--     r29,0x1F0+var_13C(r1)` 0x825fa9c8, delivered 0x825fab34) or sub_825FA078 (0x8275b36c);
--   - the pulse, every 1.2 s on the PROPS in its reach: sub_8275BE98 → sub_825FAB70, whose query
--     (`li r7,4` 0x825fac68, `bl sub_826017D8` 0x825facb8) takes an entity whose +0x78 component's
--     kind is 4 (sub_822035E0's default case) — the kind the update counts as a prop (`lwz
--     r3,0x78(r11)` 0x8275b88c, `cmpwi cr6,r29,4` 0x8275b8b4; a creature is 2) — and hits each
--     through sub_825FB480 (serial +0x18, 0 damage, `bl sub_825FB480` 0x825fad9c);
--   - the drop: the release's hit on a creature it lets go (`bl sub_825FA680` 0x8275d034) carries
--     NO serial (`li r5,0` 0x8275d01c) and 0 damage, so it names no cast and rides nothing.
-- So the hit of a vortex cast on a living creature the live vortex does not hold is its shudder;
-- every other hit of the cast is for an entity on its held list, or a prop.

-- Whether the live vortex of cast `serial` holds `victim` now (its own held list).
local function VortexHolds(serial, victim)
	local held = Refii.vortex_held(serial)
	if held == nil then return false end
	for i = 1, #held do
		if held[i] == victim then return true end
	end
	return false
end

-- Whether a hit of vortex cast `serial` on `victim` is its shudder: the live vortex does not hold
-- the victim (nil held list: no live vortex, nothing to tell).
local function VortexShudder(serial, victim)
	local held = Refii.vortex_held(serial)
	if held == nil then return false end
	for i = 1, #held do
		if held[i] == victim then return false end
	end
	return true
end

-- A creature in the world that has not died: the creature component (Refii.creature_tier non-nil,
-- the component the vortex's own classification reads, rex_VortexComponent_Classify) and living.
-- A living creature that is not a player's hero. The vortex classes every hero as unliftable and
-- shudder-hits one walking into it (the game's own hit lands as it always does); a weave's extras —
-- the extra damage, the status, the shove, the Blades — never fall on either player (both are
-- principals), which Fable III's rule, with no co-op partner of its own, does not have to say.
local function LivingCreature(e)
	return e ~= nil and e ~= 0 and Refii.creature_tier(e) ~= nil and Refii.living(e) and
		(Refii.player_of(e) or 0) == 0
end

-- THE PARTNER SPELL, cast by the game's own factory with the hero as caster: a Targeted cast on
-- the victim, or — a fireball that hit nothing alive — at the impact point.
--
-- A POSITION CAST, as Fable III's weave is: every request there is "shot from the caster" = 0, and
-- Lightning, Fireball and Force Push resolve at once at the request position with the hit on the
-- target (Fable III Lightning Start sub_82B02B10 @0x82b02b58, Fireball Start sub_82AF7F60
-- @0x82af8110, Force Push Start sub_82AFFB60 @0x82affbd8) — no bolt from the caster, no projectile,
-- no hand effect: the pair reads as ONE spell whose hits carry the second effect. Refii.cast_spell's
-- `at` is that cast: the core places the partner on the victim or the point by the spell's own rule
-- (Godot/docs/modding/gameplay.md, "Casting at a target or a point"), from the BASE's own place
-- (`pos`: the vortex, the fireball's impact, the stab — a push shoves away from there, as Fable
-- III's request direction has it) and facing `dir` where the base stands on the place.
--
-- The cast carries its base's tag negated and Fable III's damage share (`damage_scale`, applied
-- by the hit receiver to every hit of the cast where the hit is decided, a bolt landed inside the
-- factory included); its status takes `weave_duration` on `status_applying`.
local function CastPartner(cast)
	local hero = Refii.hero(cast.player)
	if hero == nil or not Refii.world_here() or (cast.victim ~= 0 and not Refii.alive(cast.victim)) then return end
	-- A vortex's partner goes only to the creature it is holding when the delay runs out.
	if cast.held ~= 0 and not VortexHolds(cast.held, cast.victim) then return end
	local at = cast.victim
	if at == 0 then at = { cast.where[1], cast.where[2], cast.where[3] } end
	local spell, serial = Refii.cast_spell(hero, cast.type, true, 0, cast.level, cast.origin, cast.facing,
		{ at = at, tag = cast.tag, damage_scale = Knob("weave_damage", 0.7) })
	if cast.victim ~= 0 then
		Refii.log("Spell Weaving: partner " .. cast.type .. " cast on " .. cast.victim .. " at power " .. (cast.level + 1) ..
			" for player " .. cast.player .. " (spell " .. tostring(spell) .. " serial " .. tostring(serial) .. ")")
	else
		Refii.log("Spell Weaving: partner " .. cast.type .. " cast at (" .. cast.where[1] .. ", " .. cast.where[2] .. ", " ..
			cast.where[3] .. ") at power " .. (cast.level + 1) .. " for player " .. cast.player .. " (spell " .. tostring(spell) ..
			" serial " .. tostring(serial) .. ")")
	end
end

-- The partner casts waiting for their delay (Refii.after), at most kPending: on a victim, or — a
-- fireball that hit nothing alive — at a point, facing a direction, each carrying where its base
-- stands (a push is away from there). One per player, type and victim: a second request
-- refreshes its power and keeps its time, as Fable 3's delayed caster reuses the slot of the same
-- spell type; each impact at a point is its own cast.
local kPending = 24
local pending = {}
local pending_count = 0
local at_point_next = 0

local function Queue(key, cast, delay)
	if pending[key] ~= nil then
		pending[key].level = cast.level
		return
	end
	if pending_count >= kPending then return end
	pending[key] = cast
	pending_count = pending_count + 1
	Refii.after(delay, function()
		pending[key] = nil
		pending_count = pending_count - 1
		CastPartner(cast)
	end, key)
end

-- `held`: a vortex's serial, whose held list must still hold the victim at the cast; 0 none.
local function QueuePartner(w, level, victim, delay, origin, held)
	Queue("spell_weaving.partner." .. w.player .. "." .. w.overlay .. "." .. victim, {
		player = w.player, type = w.overlay, level = level, victim = victim, tag = -w.tag, held = held or 0,
		where = { 0, 0, 0 }, facing = { 0, 0, 1 }, origin = { origin[1], origin[2], origin[3] },
	}, delay)
end

local function QueuePartnerAt(w, level, where, facing, delay)
	at_point_next = at_point_next % kPending + 1
	Queue("spell_weaving.partner_at." .. at_point_next, {
		player = w.player, type = w.overlay, level = level, victim = 0, tag = -w.tag, held = 0,
		where = { where[1], where[2], where[3] }, facing = { facing[1], facing[2], facing[3] },
		origin = { where[1], where[2], where[3] },
	}, delay)
end

-- A partner's status lasts Fable 3's share of its own duration (WeaveSpellMultipliers): the
-- electrify, chaos or burn a partner cast installs carries that cast's tag.
Refii.on("status_applying", function(e)
	if IsPartnerTag(e.tag) then e.seconds = e.seconds * Knob("weave_duration", 0.7) end
end)

----------------------------------------------------------------------------------------------------
-- THE LOOK, PLAYED PER CAST. Each pair has its own mod effect, fx/effects/spell_weaving_<base>_
-- <overlay>.json, built from the particle bank's own entities so both looks draw it. A weave plays
-- it with Refii.play_fx_on on each thing its base put in the world — the vortex entity, the
-- fireball's shot, each live sword, each victim of a bolt, shove or chaos while held — and
-- Refii.stop_fx ends each play when that thing is gone; with nothing to ride (a fireball's blast,
-- an instant cast that hit nobody) it plays once at the point. fx/recipes.json tints each base
-- effect by the play bound to it ("with"), not by a table.
----------------------------------------------------------------------------------------------------

local kLookNames = {
	[kLightning] = "shock", [kFireball] = "fire", [kSwords] = "blades",
	[kVortex] = "vortex", [kChaos] = "chaos", [kForcePush] = "push",
}

-- The pair's own effect; nil for a pair with none: a spell woven with itself under allow_same, and
-- a Force Push that is only Fireball's or Blades' knockdown (Shock + Force Push keeps its burst).
local function LookName(base, overlay)
	if base == overlay or kLookNames[base] == nil or kLookNames[overlay] == nil then return nil end
	if KnockdownOverlay(base, overlay) and base ~= kLightning then return nil end
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

-- Tints each of a Blades weave's swords not yet tinted by its partner: on its arrival, and again
-- on a later tick for one the setter refused.
local function TintSwords(w)
	if w.base ~= kSwords then return end
	local r, g, b
	for i = 1, #w.swords do
		local sword = w.swords[i]
		if not w.sword_tinted[sword] then
			if r == nil then
				r, g, b = SwordTint(w.overlay)
				if r == nil then return end
			end
			if Refii.entity_tint(sword, r, g, b, 1) then w.sword_tinted[sword] = true end
		end
	end
end

-- What a weave's look rides this tick: entities (each its own play), or — with none, while the
-- base is out — the point at the weave's origin (0). Each sword, each victim of a bolt, shove or
-- chaos the base still holds, else the anchor (the vortex, the shot in flight), else the origin
-- (an instant cast that hit nobody). A base's end (the vortex's shutdown, the fireball's blast)
-- ends its look: a play restarted at the end point would pop for its short hold.
local function Riders(w, live)
	local out = {}
	if w.base == kSwords then
		for i = 1, #w.swords do
			if #out < kMaxLooks then out[#out + 1] = w.swords[i] end
		end
		return out
	end
	if w.base == kLightning or w.base == kForcePush or w.base == kChaos then
		local now = Refii.now()
		for i = 1, #w.victims do
			local v = w.victims[i]
			if #out < kMaxLooks and Refii.alive(v.e) and Holding(w, v, now) then out[#out + 1] = v.e end
		end
		if #out > 0 then return out end
	end
	if w.anchor ~= 0 or live then out[1] = w.anchor end  -- 0: the point
	return out
end

-- Each live weave plays its look on what it rides, and stops each play whose rider has gone.
local function UpdateLooks()
	local now = Refii.now()
	for n = 1, #weaves do
		local w = weaves[n]
		local live = BaseLive(w)
		local holding = not live and w.done_at >= 0 and now - w.done_at <= HoldSeconds(w.base)
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
-- THE LAUNCH AND THE CAST'S OWN EVENTS
----------------------------------------------------------------------------------------------------

Refii.on("spell_launch", function(e)
	local p = e.player
	if not IsPlayer(p) or e.slot < 0 or e.slot >= kSlots then return end

	-- Fable 3 pacing: one cast at the charged power. The cascade's lower slots are skipped; the
	-- cascade itself runs on and reaches the top slot regardless of what the launch did.
	if e.cascading and Knob("top_only", 1) ~= 0 then return true end

	local primary = e.type
	local partner = LearnedPartner(p, e.slot)
	local weave = Knob("woven", 1) ~= 0 and e.slot + 1 <= Level(p) and Weavable(primary) and partner >= 0 and
		(primary ~= partner or Knob("allow_same", 0) ~= 0)
	if not weave then return end

	local top = primary
	if kRank[primary] > kRank[partner] then top = partner end
	local under = primary
	if top == primary then under = partner end
	-- The base is the one cast, carrying this weave's tag; the overlay is never launched here —
	-- the base's hits cast it on each victim.
	local w = AddWeave(NextTag(), p, top, under, e.slot)
	local x, y, z = Refii.position(e.caster)
	if x ~= nil then w.origin = { x, y, z } end
	e.type = top
	e.tag = w.tag
	Refii.log("Spell Weaving: cast " .. top .. " carrying " .. under .. " at power " .. (e.slot + 1) .. " for player " .. p ..
		" (slot held " .. primary .. ", tag " .. w.tag .. ")")
end)

-- Inside the factory, before spell_created: the cast's vortex, the anchor its look rides, its hit
-- damage, and whether it lifts — on every machine running the cast, each copy its own vortex.
Refii.on("vortex_started", function(e)
	local w = WeaveOf(e.tag, e.serial)
	if w == nil or w.base ~= kVortex or e.entity == 0 then return end
	w.vortex_live = true
	w.vortex_damage = e.damage
	w.vortex_born = Refii.now()
	local balance = Refii.spell_balance(e.caster, kVortex, e.level)
	if balance ~= nil and balance.duration ~= nil then w.vortex_duration = balance.duration end
	if Refii.position(e.entity) ~= nil then w.anchor = e.entity end
	if kVortexPass[w.overlay] ~= nil and Knob("woven", 1) ~= 0 then e.lifts = false end
end)

-- Inside the factory, before spell_created: each sword of the cast (and those a re-cast adds to
-- its live set), tinted on arrival; a sword leaves the weave with the world (entity_removed).
Refii.on("summoned", function(e)
	if e.kind ~= kSummonSword then return end
	local w = WeaveOf(e.tag, e.serial)
	if w == nil or w.base ~= kSwords then return end
	for i = 1, #w.swords do
		if w.swords[i] == e.summon then return end
	end
	if #w.swords >= kMaxSwords then return end
	w.swords[#w.swords + 1] = e.summon
	TintSwords(w)
end)

Refii.on("entity_removed", function(e)
	for n = 1, #weaves do
		local swords = weaves[n].swords
		for i = #swords, 1, -1 do
			if swords[i] == e.entity then
				table.remove(swords, i)
				weaves[n].sword_tinted[e.entity] = nil
			end
		end
	end
end)

-- The launch's spell object: its power, and the fireball's shot as the anchor its look rides.
Refii.on("spell_created", function(e)
	local w = WeaveOf(e.tag, e.serial)
	if w == nil and IsBaseTag(e.tag) and IsPlayer(e.caster_player) then
		-- Online, the copy of a cast the peer decided can take the peer's tag as the factory makes
		-- it, before any event carried this machine's tag. The factory runs inside the launch, so the
		-- weave is the one that launch made: unbound until now, of the same player and `slot`.
		for i = #weaves, 1, -1 do
			local c = weaves[i]
			if c.serial == 0 and c.player == e.caster_player and c.slot == e.slot and c.base == e.type then
				w = c
				w.serial = e.serial
				w.adopted = e.tag
				break
			end
		end
	end
	if w == nil or e.type ~= w.base then return end
	if e.level >= 0 and e.level < kLevels then w.level = e.level end
	if w.base == kFireball then
		local parts = Refii.spell_parts(e.serial)
		local shot = parts and parts.anchor
		if shot and Refii.position(shot) ~= nil then w.anchor = shot end
	elseif (w.base == kLightning or w.base == kForcePush or w.base == kChaos) and e.targeted and e.target ~= 0 and
		not Refii.world_here() then
		-- Where hits are not decided (an online joiner) the victims arrive as the host's relayed
		-- hits, a network moment later: until then the look rides on the target the cast states.
		NoteVictim(w, e.target)
	end
	Refii.log("Spell Weaving: weave serial " .. w.serial .. " = " .. w.base .. " carrying " .. w.overlay .. " at power " ..
		(w.level + 1) .. " for player " .. w.player .. " (anchor " .. w.anchor .. ", vortex " .. tostring(w.vortex_live) ..
		", swords " .. #w.swords .. ")")
end)

-- The registry took the spell out: the base's own end, whatever ended it.
Refii.on("spell_ended", function(e)
	local w = WeaveOf(e.tag, e.serial)
	if w ~= nil and w.serial == e.serial then w.ended = true end
end)

----------------------------------------------------------------------------------------------------
-- THE HITS
----------------------------------------------------------------------------------------------------

-- Fable III: WeaveSpellShotDelayTime, 0.25 s in the vortex records and 1 s by default, ±5 %
-- (sub_8265FD30 -> sub_8259F7F0(time, 0.1)). A vortex's never reaches the end of the twister's life:
-- at `vortex_delay_cap` (0.95) of the time it has left or later, it is drawn again below that, from
-- 0.01 (sub_82A33F30: `lfs f13,0x54(r30); fmuls f12,f13,f0` 0x82a34090..98, the floor flt_832A8370
-- 0x82a340a8, `fcmpu f1,f13; blt` 0x82a340d0..d4, the draw 0x82a340e0..).
local function ShotDelay(w)
	local delay
	if w.base == kVortex then delay = Knob("shot_delay_vortex", 0.25) else delay = Knob("shot_delay", 1.0) end
	delay = delay * Jitter()
	if w.base == kVortex and w.vortex_duration > 0 then
		local cap = math.max((w.vortex_duration - (Refii.now() - w.vortex_born)) * Knob("vortex_delay_cap", 0.95), 0.01)
		if delay >= cap then delay = 0.01 + math.random() * (cap - 0.01) end
	end
	if delay < 0 then delay = 0 end
	return delay
end

-- Where the base stands, the origin its partner comes from (a push shoves away from here): the
-- live twister's point as spell_parts states it, else the anchor, else for a stab or a bolt the
-- side of `attacker` (the base stands where its hit landed), else the weave's origin.
local function BaseOrigin(w, attacker)
	local origin = { w.origin[1], w.origin[2], w.origin[3] }
	if w.base == kVortex and w.vortex_live then
		local parts = Refii.spell_parts(w.serial)
		if parts and parts.origin then origin = { parts.origin[1], parts.origin[2], parts.origin[3] } end
	elseif w.anchor ~= 0 and Refii.alive(w.anchor) then
		local x, y, z = Refii.position(w.anchor)
		if x ~= nil then origin = { x, y, z } end
	elseif w.base == kSwords or w.base == kLightning then
		local x, y, z = Refii.position(attacker)
		if x ~= nil then origin = { x, y, z } end
	end
	return origin
end

-- THE BASE'S OWN HITS CARRY THE OVERLAY where no partner is cast, before delivery and where the
-- hit is decided: a Force Push overlay is the hit's knockdown (the byte Force Push's own hit sets,
-- read by the victim's hit response to choose the knockdown), and a pass-through vortex's shudder
-- on a living creature (VortexShudder: not its pulse on a prop, nor a hit on a prop it lifted)
-- lands at least the vortex's own hit (vortex_started's `damage`: what each hit on a held creature
-- starts from, which a vortex that lifts nobody never deals), Fire and Shock `weave_damage` of it
-- on top.
Refii.on("hit", function(e)
	if not IsBaseTag(e.tag) or e.spell_type < 0 or Knob("woven", 1) == 0 or Refii.event_replica() or not Refii.world_here() then
		return
	end
	local w = WeaveOf(e.tag, e.spell_serial)
	if w == nil or e.spell_type ~= w.base then return end
	if KnockdownOverlay(w.base, w.overlay) then
		e.knockdown = true
	elseif w.base == kVortex and kVortexPass[w.overlay] ~= nil then
		if not VortexShudder(w.serial, e.victim) or not LivingCreature(e.victim) then return end
		local damage = math.max(e.damage, w.vortex_damage)
		if kVortexPass[w.overlay].extra then damage = damage * (1 + Knob("weave_damage", 0.7)) end
		e.damage = damage
	end
end)

-- A pass-through vortex's shudder on a living creature put the overlay on it, once per victim per
-- cast (the pulse on a prop and a hit on a prop it lifted carry nothing): the burn,
-- the shock or the chaos through the spell's own installer for `weave_duration` of the time the
-- overlay's own Start gives it at that power (Refii.spell_balance: the seconds its class's data read
-- loads, plus the Start's draw in [0, spread)), or Force Push's knockdown hit away from the
-- twister's point (spell_parts), on the next tick.
local function PassThrough(w, e)
	local pass = kVortexPass[w.overlay]
	if not VortexShudder(w.serial, e.victim) or not LivingCreature(e.victim) then return end
	if not Fresh(e.victim, w.tag, w.overlay) then return end
	local level = w.level
	if e.spell_level >= 0 and e.spell_level < kLevels then level = e.spell_level end
	if pass.status ~= 0 then
		local hero = Refii.hero(w.player)
		local balance = hero and Refii.spell_balance(hero, w.overlay, level)
		if balance == nil or balance.status_seconds == nil then return end
		local seconds = (balance.status_seconds + math.random() * balance.status_spread) * Knob("weave_duration", 0.7)
		Refii.apply_status(e.victim, pass.status, seconds, level, hero)
		return
	end
	local from = BaseOrigin(w, e.attacker)
	local x, y = Refii.position(e.victim)
	if x == nil then return end
	local dx, dy = x - from[1], y - from[2]
	local length = math.sqrt(dx * dx + dy * dy)
	if length < 0.001 then return end
	local victim = e.victim
	dx, dy = dx / length, dy / length
	Refii.after(0, function()
		if Refii.alive(victim) then Refii.knockback(victim, dx, dy, 0, 1) end
	end, "spell_weaving.push." .. victim)
end

-- THE VORTEX CASTS BLADES ON EACH CREATURE IT REACHES, by Fable III's rule (its vortex entity,
-- sub_82A34F88 / sub_82A33B30 / sub_82A33D40 / sub_82A33F30 for a creature it holds, sub_82A33C58
-- for one it cannot lift):
--   - only a creature: the roll on capture is behind sub_825A5D98, the victim's creature test (`bl
--     sub_825A5D98; clrlwi r29,r3,24` 0x82a350c0..c4, `cmplwi cr6,r29,0; beq` 0x82a35364), and the
--     shudder's is behind the creature query (type 2 `stw r26,0x280+var_1E8(r1)` 0x82a32630, `bl
--     sub_82A38640` 0x82a32654). Here the capture is raised for everything the vortex lifts, props
--     included (vortex_caught), and a Blades sword or any other spell entity is no creature:
--     Refii.creature_tier names the creature component, and a creature that has died is skipped;
--   - once per creature per vortex, held or shuddered: one not on the vortex's ONE list of
--     partnered creatures (+0x228, `bl sub_823A6378` 0x82a353a8, 0x82a33cc8; the held step and the
--     shudder both append to it once a cast is queued, `bl sub_823A4030` 0x82a33ef8 / 0x82a33d20) —
--     a release and a re-capture cast nothing again, nor does a held creature shuddered later or
--     the reverse;
--   - the roll: WeaveSpellAffectProbability (100 at every power of the vortex combos) against a
--     uniform draw (`fmuls f9,f10,f11; fcmpu f9,f0; bgt` 0x82a33bbc..c4), then for a held creature
--     the moment: between `vortex_window_start` and `vortex_window_end` (15 % and 60 %) of the
--     vortex's Duration, never before now (`lfs f13` 0.15 0x82a33bd4, `lfs f12` 0.6 0x82a33bf0, the
--     draw `fmadds f6,f7,f12,f13` 0x82a33c44; 0.1 s once 60 % has gone, the roll's first store
--     0x82a33b4c..54) — the in-vortex timer, whose end (`stb r9,0x3A(r31)` 0x82a29fd8) lets the
--     vortex's step for that held creature cast;
--   - the held cast: still held then (the step runs for held creatures only), the creature is
--     partnered and the Blades cast follows the vortex's shot delay (ShotDelay), as Fable III's
--     request goes to its delayed caster; checked held again when the delay runs out (CastPartner);
--   - the shudder cast: a creature the vortex cannot lift takes its one shudder hit per cast (Fable
--     III `cmpwi cr6,r11,2` 0x82a32758, the CECVortexShudderHit ring of the cast's serial +0x14,
--     the hit `bl sub_825A2AD0` 0x82a329c0 / `bl sub_825A2780` 0x82a32a60) and sub_82A33C58 right
--     after it (`bl sub_82A33C58` 0x82a329cc, 0x82a32a6c): in the world (`lbz r11,0x94(r4)`
--     0x82a33c78), the roll for its probability only — the moment it stores goes to the stack slot
--     sub_823A6378's answer then overwrites (`addi r4,r1,0x80+var_30` 0x82a33c9c, `addi
--     r3,r1,0x80+var_30` 0x82a33cbc) — not partnered, then cast at once with the same shot delay
--     (`bl sub_82A33F30` 0x82a33ce4) and partnered when it queued one (`fcmpu cr6,f1,f0; blt`
--     0x82a33cf0..f4 against 0.0); no hold and no held check. Fable 2's shudder is the same one hit
--     per cast per creature (`sub_8275EB10` answering 2, `cmpwi cr6,r11,2` 0x8275b008, the ring of
--     the cast's serial `lwz r9,0x18(r22)` 0x8275b11c), arriving as the cast's `hit_landed` on a
--     living creature the live vortex does not hold (THE VORTEX'S HITS, above; each held entry is
--     let go only after its hits, `bl sub_8275CB28` 0x8275be18). Fable III re-rolls at
--     each later half-second pass the ring skips (`bne cr6,loc_82A32A64` 0x82a32880); Fable 2's
--     ring skip raises nothing (`bne cr6,loc_8275B380` 0x8275b168), so here the roll is made once,
--     at the hit — the same at the shipped 100;
--   - the swords: Fable III's weave makes vortex_swords_<power> (3, 4, 4, 4, 5); Fable II's Blades
--     makes what its power's own record states (Refii.spell_balance's `swords`, 3/4/5/6/8 in the
--     shipped data), so the partner is cast at the highest power up to the vortex's that makes no
--     more than Fable III's count.
-- A Blades sword's hits carry the partner's tag and cast nothing (hit_landed), nor do the vortex's
-- own hits on what it holds. A pass-through vortex lifts no creature. Partners are
-- cast where the world runs; the copy of the cast on an online joiner raises its captures as
-- replicas, and casts none.

-- Fable III's probability, the first draw of sub_82A33B30.
local function VortexAffects()
	return math.random() * 100 <= Knob("vortex_affect_probability", 100)
end

-- Fable III's roll (sub_82A33B30): nil when it fails, else how long until the partner's moment.
local function VortexRoll(w)
	if not VortexAffects() then return nil end
	local duration = w.vortex_duration
	if duration <= 0 then return 0 end
	local elapsed = Refii.now() - w.vortex_born
	local first = math.max(duration * Knob("vortex_window_start", 0.15), elapsed)
	local last = duration * Knob("vortex_window_end", 0.6)
	if last <= elapsed then return 0.1 end
	return math.random() * (last - first) + first - elapsed
end

-- The Blades power whose own swords per cast are at most Fable III's for the vortex's power.
local function VortexBladesLevel(w, hero)
	if w.blades_level >= 0 then return w.blades_level end
	local want = Knob("vortex_swords_" .. (w.level + 1), kVortexSwords[w.level + 1])
	local level = 0
	for power = 0, w.level do
		local balance = Refii.spell_balance(hero, kSwords, power)
		if balance ~= nil and balance.swords ~= nil and balance.swords <= want then level = power end
	end
	w.blades_level = level
	return level
end

-- The roll's moment came: a creature the vortex still holds is partnered and its Blades queued.
local function VortexPartner(w, victim, serial)
	w.waiting[victim] = nil
	local hero = Refii.hero(w.player)
	if hero == nil or w.partnered[victim] or not VortexHolds(serial, victim) or not LivingCreature(victim) then return end
	w.partnered[victim] = true
	QueuePartner(w, VortexBladesLevel(w, hero), victim, ShotDelay(w), BaseOrigin(w, hero), serial)
end

-- A creature the vortex cannot lift took its shudder hit (sub_82A33C58): rolled, and unless already
-- partnered, partnered with its Blades queued now, the vortex's shot delay later.
local function VortexShudderPartner(w, e)
	local victim = e.victim
	if not VortexShudder(w.serial, victim) or not LivingCreature(victim) or w.partnered[victim] then return end
	if not VortexAffects() then return end
	local hero = Refii.hero(w.player)
	if hero == nil then return end
	w.partnered[victim] = true
	QueuePartner(w, VortexBladesLevel(w, hero), victim, ShotDelay(w), BaseOrigin(w, hero))
end

-- The hit landed first, as the game delivers it; the partner's cast follows it — the partner of
-- the WEAVE whose tag the hit carries. CO-OP: hits are decided where the world runs, so partners
-- are cast where Refii.world_here holds (the host, or the one machine alone and in local co-op); a
-- relayed hit (an online joiner) only notes its victim for the look. The core hands a relayed hit
-- over with this machine's serial of the cast and that spell's tag once it has paired the two
-- copies (holding it up to a second for a spell not made here yet); a hit still unpaired after that
-- carries the host's numbers, which name nothing here (Refii.host_serial is nil for them), and is
-- left alone — the cast's later events, by its tag, carry the look.
Refii.on("hit_landed", function(e)
	if Knob("woven", 1) == 0 or e.spell_type < 0 or IsPartnerTag(e.tag) then
		return  -- a weave-side spell never casts a partner of its own
	end
	if Refii.event_replica() then
		local paired = Refii.host_serial(e.spell_serial)
		if paired == nil or paired == 0 then return end
		local w = WeaveOf(e.tag, e.spell_serial)
		if w and e.spell_type == w.base then NoteVictim(w, e.victim) end
		return
	end
	local w = WeaveOf(e.tag, e.spell_serial)
	if w == nil or e.spell_type ~= w.base or w.overlay < 0 then return end
	NoteVictim(w, e.victim)
	if not Refii.world_here() or not Refii.alive(e.victim) or e.victim == e.attacker then return end
	if w.base == kVortex then
		-- A pass-through vortex's hit carries its overlay; a lifting one casts on the creatures it
		-- holds (vortex_caught) and on those its shudder hits.
		if kVortexPass[w.overlay] ~= nil then PassThrough(w, e) else VortexShudderPartner(w, e) end
		return
	end
	if KnockdownOverlay(w.base, w.overlay) or not Fresh(e.victim, w.tag, w.overlay) then return end
	local level = w.level
	if e.spell_level >= 0 and e.spell_level < kLevels then level = e.spell_level end
	QueuePartner(w, level, e.victim, ShotDelay(w), BaseOrigin(w, e.attacker))
end)

Refii.on("vortex_caught", function(e)
	if not e.caught or Knob("woven", 1) == 0 or IsPartnerTag(e.tag) or Refii.event_replica() then return end
	local w = WeaveOf(e.tag, e.serial)
	if w == nil or w.base ~= kVortex or w.overlay < 0 or kVortexPass[w.overlay] ~= nil then return end
	local victim = e.victim
	if not Refii.world_here() or victim == e.caster then return end
	if not LivingCreature(victim) or w.partnered[victim] or w.waiting[victim] then return end
	local wait = VortexRoll(w)
	if wait == nil then return end
	w.waiting[victim] = true
	local serial = e.serial
	Refii.after(wait, function() VortexPartner(w, victim, serial) end, "spell_weaving.vortex." .. w.tag .. "." .. victim)
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
	local w = WeaveOf(e.tag, e.serial)
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
	w.done_at = Refii.now()  -- the look holds on the blast for its fade; the projectile is gone
	if e.hits ~= 0 or Knob("woven", 1) == 0 or w.overlay < 0 or not Refii.world_here() then return end
	-- Chaos turns only its target: at a point it has nothing to act on (cast_spell refuses it). A
	-- Force Push overlay is the blast's knockdown, never a cast.
	if w.overlay == kChaos or KnockdownOverlay(w.base, w.overlay) then return end
	local level = w.level
	if e.level >= 0 and e.level < kLevels then level = e.level end
	local delay = Knob("shot_delay", 1.0) * Jitter()
	if delay < 0 then delay = 0 end
	QueuePartnerAt(w, level, { e.x, e.y, e.z }, { e.nx, e.ny, e.nz }, delay)
	Refii.log("Spell Weaving: fireball serial " .. w.serial .. " struck nothing alive at (" .. e.x .. ", " .. e.y .. ", " .. e.z ..
		"); partner " .. w.overlay .. " queued there")
end)

----------------------------------------------------------------------------------------------------
-- THE TICK
----------------------------------------------------------------------------------------------------

Refii.on("tick", function()
	local now = Refii.now()
	local n = 1
	while n <= #weaves do
		local w = weaves[n]
		local over = false
		if w.serial == 0 then
			-- The factory runs inside the launch: a weave no event of its cast reached by the next
			-- tick belonged to a launch that made no spell (no will, no target, refused after this mod).
			over = true
		else
			if not BaseLive(w) and w.done_at < 0 then w.done_at = now end
			over = (w.done_at >= 0 and now - w.done_at > HoldSeconds(w.base) + 1.0) or Refii.hero(w.player) == nil
		end
		if over then
			Refii.log("Spell Weaving: weave tag " .. w.tag .. " serial " .. w.serial .. " over")
			RemoveWeave(n)
		else
			n = n + 1
		end
	end

	for i = 1, #weaves do TintSwords(weaves[i]) end
	UpdateLooks()
end)

W.Weavable = Weavable
W.Rank = kRank
W.HoldSeconds = HoldSeconds
W.LookName = LookName
