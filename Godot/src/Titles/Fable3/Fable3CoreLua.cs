namespace Refii.Titles.Fable3;

using System.Text;
using Refii.Bnk;

/// <summary>
/// Fable 3's helper calls for mods' Lua: the gameplay half of the Fable 2 surface (health, gold,
/// items, stats, position, sound, spawning, time), written over the game's own natives as the shipped
/// scripts call them. Run in the game VM before any mod's init.lua (Fable3/src/hooks/src/
/// mod_lua_hooks.cpp runs <see cref="Key"/> first), so a mod finds them on <c>Refii</c>. Every call
/// takes a handle or a game Entity; a dead or unknown one answers nil and changes nothing.
/// </summary>
/// <remarks>
/// The natives' forms are the shipped scripts' own (read from the decompiled scripts, 2026-11-16):
/// <c>Money.Add(e, n, EMoneyChangeType.INCOME_TYPE_SCRIPT)</c> / <c>Money.Remove(e, n, type)</c>
/// (moneyenums.lua: INCOME_TYPE_SCRIPT 6, OUTGOING_TYPE_OTHER 31), <c>Health.Get/GetMax(e)</c>,
/// <c>Health.Modify(e, delta, source)</c>, <c>Physics.TeleportToPosition(e, CVector3(x, y, z))</c>,
/// <c>e:GetPosition()</c> (a vector with GetX/GetY/GetZ), <c>Inventory.AddItemOfType(e, name)</c>,
/// <c>Inventory.GetNumberOfItemsOfType(e, name)</c>, <c>Inventory.RemoveItemOfType(e, name)</c>,
/// <c>Stats.GetMorality/ModifyMorality(e, d)</c>, <c>Stats.GetRenownTotal(e)</c>,
/// <c>Stats.ModifyRenown(e, d)</c>, <c>Stats.GetNumberOfGuildSeals/ModifyGuildSeals(e, n)</c>,
/// <c>Sound.PlayEvent(e, cue, tag)</c>, <c>Debug.CreateEntityAtPosition(def, name, position)</c>,
/// <c>Timing.GetTimeOfDay()</c> / <c>Timing.SetTimeOfDay(h)</c>. Items are named by their definition
/// name ("ObjectInventoryWeaponRifleBasic"), as Fable 3's scripts name them.
/// </remarks>
public static class Fable3CoreLua
{
	public const string Key = "refii/core_init.lua";

	public const string Source = """
Refii = Refii or {}
-- PAIRS TAKES TWO LOOP VARIABLES HERE. Fable 3's VM (Havok Script) swaps the global `pairs` for
-- `kpairs` at state creation (TU3 sub_822E5308), whose generator is a `return 0` stub the VM's
-- generic-for walks inline only for exactly two loop variables (TFORLOOP `cmpwi r31,2` at
-- 0x822A46E0); `for k in pairs(t)` runs zero times. Write `for k, v in pairs(t)` (or `for k in next, t`).
local E = function(e) return Refii.entity and Refii.entity(e) or nil end
local INCOME_SCRIPT, OUTGOING_OTHER = 6, 31
-- This machine's own hero, the game's own answer: player 1 alone, in couch co-op and on an online host;
-- on an online client player 1 is the host's hero.
function Refii.local_hero() local h = GetLocalHero and GetLocalHero() if h == nil then return nil end return Refii.handle(h) end
function Refii.health(e) local x = E(e) if x == nil then return nil end return Health.Get(x), Health.GetMax(x) end
function Refii.set_health(e, v) local x = E(e) if x == nil then return false end Health.Modify(x, v - Health.Get(x), x) return true end
function Refii.modify_health(e, delta, attacker) local x = E(e) if x == nil then return false end
	Health.Modify(x, delta, attacker ~= nil and E(attacker) or x) return true end
function Refii.set_invulnerable(e, on) local x = E(e) if x == nil then return false end Health.SetAsInvulnerable(x, on and true or false) return true end
function Refii.position(e) local x = E(e) if x == nil then return nil end local p = x:GetPosition() return p:GetX(), p:GetY(), p:GetZ() end
function Refii.set_position(e, px, py, pz) local x = E(e) if x == nil then return false end
	Physics.TeleportToPosition(x, CVector3(px, py, pz)) return true end
function Refii.name(e) local x = E(e) if x == nil then return nil end return x:GetName() end
function Refii.gold(e) local x = E(e) if x == nil then return nil end return Money.Get(x) end
function Refii.give_gold(e, delta) local x = E(e) if x == nil then return false end
	if delta > 0 then Money.Add(x, delta, INCOME_SCRIPT) elseif delta < 0 then Money.Remove(x, - delta, OUTGOING_OTHER) end
	return true end
function Refii.item_count(e, def) local x = E(e) if x == nil then return 0 end return Inventory.GetNumberOfItemsOfType(x, def) end
function Refii.add_item(e, def, n) local x = E(e) if x == nil then return 0 end n = n or 1
	for i = 1, n do Inventory.AddItemOfType(x, def) end return n end
function Refii.remove_item(e, def, n) local x = E(e) if x == nil then return 0 end n = n or 1
	local removed = 0 for i = 1, n do if Inventory.GetNumberOfItemsOfType(x, def) < 1 then break end
	Inventory.RemoveItemOfType(x, def) removed = removed + 1 end return removed end
function Refii.morality(e) local x = E(e) if x == nil then return nil end return Stats.GetMorality(x) end
function Refii.modify_morality(e, delta) local x = E(e) if x == nil then return false end Stats.ModifyMorality(x, delta) return true end
function Refii.renown(e) local x = E(e) if x == nil then return nil end return Stats.GetRenownTotal(x) end
function Refii.modify_renown(e, delta) local x = E(e) if x == nil then return false end Stats.ModifyRenown(x, delta) return true end
function Refii.guild_seals(e) local x = E(e) if x == nil then return nil end return Stats.GetNumberOfGuildSeals(x) end
function Refii.give_guild_seals(e, n) local x = E(e) if x == nil then return false end Stats.ModifyGuildSeals(x, n) return true end
function Refii.play_sound(cue, e, tag) local x = E(e) if x == nil then return false end Sound.PlayEvent(x, cue, tag or cue) return true end
-- play_sound_at(cue, x, y, z [, tag]): the cue at a point (guest axes) on a sound player of the API's own,
-- eight kept (mod_lua_hooks.cpp _play_sound_at); stop_sound(0, tag) stops a tag on them.
function Refii.play_sound_at(cue, x, y, z, tag)
	if Refii._play_sound_at == nil or type(cue) ~= "string" or type(x) ~= "number" or type(y) ~= "number" or type(z) ~= "number" then return false end
	return Refii._play_sound_at(cue, x, y, z, tag or cue)
end
function Refii.spawn(def, px, py, pz, name)
	local x = Debug.CreateEntityAtPosition(def, name or def, CVector3(px, py, pz))
	if x == nil then return nil end return Refii.handle(x) end
function Refii.time_of_day() return Timing.GetTimeOfDay() end
function Refii.set_time_of_day(hours) Timing.SetTimeOfDay(hours) return true end
local SA_PLAY, SA_LOOP = 0, 49 -- scriptableactionenum.lua: PLAY_ANIMATION = 0, LOOP = 49
local function ON(v) return v == true or (type(v) == "number" and v ~= 0) end
local function V3(v) return v:GetX(), v:GetY(), v:GetZ() end

-- identity / database
function Refii.definition(e) local x = E(e) if x == nil then return nil end
	local r = x:GetRecord() if r:IsNull() then return nil end return r:GetID() end -- light userdata record ID
function Refii.uid(e) local x = E(e) if x == nil then return nil end return x:GetUIDAsString() end -- "%I64u" text
local function REC(def) if def == nil or not GDB.RecordExists(def) then return nil end return GDB.GetRecord(def) end
-- A record's ID as "0x…" text (what item_used's definition carries): the ID is a light userdata whose
-- tostring ends in its hex value.
function Refii.record_id(def) local r = REC(def) if r == nil then return nil end
	local hex = string.match(tostring(r:GetID()), "(%x+)$") if hex == nil or #hex > 8 then return nil end
	return "0x" .. string.lower(string.rep("0", 8 - #hex) .. hex) end
function Refii.record_exists(def) if def == nil then return false end return GDB.RecordExists(def) end
function Refii.record_string(def, f) local r = REC(def) if r == nil or not r:Exists(f) then return nil end return r:GetString(f) end
function Refii.record_int(def, f) local r = REC(def) if r == nil or not r:Exists(f) then return nil end return r:GetS32(f) end
function Refii.record_number(def, f) local r = REC(def) if r == nil or not r:Exists(f) then return nil end return r:GetFloat(f) end
function Refii.record_bool(def, f) local r = REC(def) if r == nil or not r:Exists(f) then return nil end return r:GetBoolean(f) end
function Refii.record_ref(def, f) local r = REC(def) if r == nil or not r:Exists(f) then return nil end
	local s = r:GetRecord(f) if s:IsNull() then return nil end return s:GetID() end

-- world queries
function Refii.near(px, py, pz, radius, creatures)
	local found = {} if not (type(radius) == "number" and radius > 0) then return found end
	local s = SearchTools.StartNewSearch(ON(creatures) and "creature" or "any")
	SearchTools.FilterWithinDistanceOfPos(s, CVector3(px, py, pz), radius)
	local d = {}
	for _, x in ipairs(SearchTools.GetSearchResults(s)) do
		local h = Refii.handle(x)
		if h ~= nil then local qx, qy, qz = V3(x:GetPosition()) qx, qy, qz = qx - px, qy - py, qz - pz
			d[h] = qx * qx + qy * qy + qz * qz found[#found + 1] = h end
	end
	table.sort(found, function(a, b) return d[a] < d[b] end)
	while #found > 2048 do table.remove(found) end
	return found end
function Refii.line_of_sight(x1, y1, z1, x2, y2, z2) return IsLineOfSightBetweenPositions(CVector3(x1, y1, z1), CVector3(x2, y2, z2)) end
function Refii.level_name() local world, level = GetLevelLoaded() return level end
function Refii.world_here() return not Network.IsClient() end
function Refii.relation(a, b) local x, y = E(a), E(b) if x == nil or y == nil then return 0 end
	if Faction.IsMyEnemy(x, y) then return 4 elseif Faction.IsMyAlly(x, y) then return 2 elseif Faction.IsNeutral(x, y) then return 1 end return 0 end

-- motion
function Refii.velocity(e) local x = E(e) if x == nil or not Physics.IsAvailable(x) then return nil end return V3(Physics.GetVelocity(x)) end
function Refii.facing(e) local x = E(e) if x == nil or not Physics.IsAvailable(x) then return nil end return V3(Physics.GetFacingVector(x)) end
function Refii.set_facing(e, fx, fy, fz) local x = E(e) if x == nil or not Physics.IsAvailable(x) then return false end
	local v = CVector3(fx, fy, fz or 0)
	if Player.IsAvailable(x) then Player.SetFacingVector(x, v) else Physics.SetFacingVector(x, v) end return true end

-- stats
function Refii.age(e) local x = E(e) if x == nil or not Age.IsAvailable(x) then return nil end return Age.GetAge(x) end
function Refii.physique(e) local x = E(e) if x == nil or not Stats.IsAvailable(x) then return nil end return Stats.GetFat(x), nil end

-- appearance (GraphicAppearance)
local function GA(e) local x = E(e) if x == nil or not GraphicAppearance.IsAvailable(x) then return nil end return x end
local function C255(v) v = (v or 0) * 255 if v < 0 then v = 0 end return math.floor(v + 0.5) end
function Refii.entity_tint(e, r, g, b, a) local x = GA(e) if x == nil then return false end
	GraphicAppearance.SetTintColour(x, C255(r), C255(g), C255(b)) return true end -- alpha: F3 always 1
function Refii.entity_untint(e) local x = GA(e) if x == nil then return false end GraphicAppearance.SetTintColour(x, 255, 255, 255) return true end
function Refii.entity_alpha(e, a) local x = GA(e) if x == nil or type(a) ~= "number" then return false end
	GraphicAppearance.SetAlpha(x, math.max(0, math.min(1, a))) return true end
function Refii.entity_scale(e, s) local x = GA(e) if x == nil or not (type(s) == "number" and s > 0) or not Physics.IsAvailable(x) then return false end
	GraphicAppearance.SetScale(x, s) return true end
function Refii.entity_saturation_mask(e, on) local x = GA(e) if x == nil then return false end GraphicAppearance.SetSaturationMask(x, ON(on)) return true end
function Refii.entity_drawable(e, on) local x = GA(e) if x == nil then return false end GraphicAppearance.SetAsDrawable(x, ON(on)) return true end

-- combat reactions (Hittable.Hit's table: Knockdown, Blockable, Damage, Hitter, Direction | FromPosition)
function Refii.knockback(e, dx, dy, dz, force) local x = E(e) if x == nil or not Hittable.IsAvailable(x) then return false end
	local f = force or 1 dx, dy, dz = dx * f, dy * f, dz * f
	if dx == 0 and dy == 0 and dz == 0 then return false end
	Hittable.Hit(x, { Knockdown = true, Blockable = false, Damage = 0, Direction = CVector3(dx, dy, dz) }) return true end
function Refii.stagger(e) local x = E(e) if x == nil or not Hittable.IsAvailable(x) then return false end
	Hittable.Hit(x, { Knockdown = false, Blockable = false, Damage = 0 }) return true end

-- animation (Action.SetCurrentAction's PLAY_ANIMATION / LOOP tables, as BehaviourBase builds them)
function Refii.play_animation(e, name, loops, blend, speed) local x = E(e)
	if x == nil or not Action.IsAvailable(x) or type(name) ~= "string" or name == "" then return false end
	loops, blend, speed = loops or 1, blend or 0.1, speed or 1
	if not (speed > 0) then return false end if blend < 0 then blend = 0 end
	local a = { Type = SA_PLAY, Anim = name, OverrideLooking = true, SpeedMultiplier = speed, BlendInTime = blend }
	if loops > 1 then a = { Type = SA_LOOP, LoopAction = a, NumLoops = loops, KillFXOnFinish = true } end
	Action.SetCurrentAction(x, a) return true end
function Refii.stop_animation(e) local x = E(e) if x == nil or not Action.IsAvailable(x) then return false end
	Action.FinishScriptedActions(x) return true end

-- movement speed (player component 37: +0xC8 factor, +0x1D overridden)
local function HERO(p) local x = E(Refii.player_hero(p)) if x == nil or not Player.IsAvailable(x) then return nil end return x end
function Refii.set_speed_multiplier(p, f) local x = HERO(p) if x == nil or type(f) ~= "number" or f < 0 then return false end
	Player.SetOverriddenMovementSpeedMultiplier(x, f) Player.SetPlayerMovementSpeedOverridden(x, true) return true end
function Refii.clear_speed_multiplier(p) local x = HERO(p) if x == nil then return false end Player.SetPlayerMovementSpeedOverridden(x, false) return true end
function Refii.speed_multiplier(p) local x = HERO(p) if x == nil then return nil end
	return Refii._speed_overridden(p), Player.GetOverriddenMovementSpeedMultiplier(x) end -- _speed_overridden: C++ (no native reads +0x1D)
function Refii.sprinting(p) local x = E(Refii.player_hero(p)) if x == nil then return false end return Sprinting.IsSprinting(x) end

-- The hero (combat, hero-systems and entity parity maps, 2026-11-16: each native's glue decompiled on
-- TU3 and disc; scratch f3/modding/parity/*/map.tsv). P(p) is player p's hero.
local P = function(p) return E(Refii.player_hero(p or 1)) end
function Refii.experience(e, pool) local x = E(e) if x == nil or not Experience.IsAvailable(x) then return nil end return Experience.Get(x, pool) end
function Refii.give_experience(e, pool, amount) local x = E(e) if x == nil or not Experience.IsAvailable(x) then return false end
	Experience.Modify(x, pool, amount) return true end
function Refii.spend_experience(e, pool, amount) local x = E(e) if x == nil or not Experience.IsAvailable(x) then return false end
	local t = { 0, 0, 0, 0, 0, 0 } t[pool + 1] = amount Experience.SpendExperienceAmounts(x, t) return true end
function Refii.ability_level(e, kind) local x = E(e) if x == nil or not Stats.IsAvailable(x) then return nil end return Stats.GetHeroAbilityLevel(x, kind) end
function Refii.inventory(e) local x = E(e) if x == nil or not Inventory.IsAvailable(x) then return nil end
	local out, i, n = {}, 0, Inventory.GetNumberOfItems(x)
	while i < n do local it = Inventory.GetItemByIndex(x, i) local c = it:GetCount() if c < 1 then break end
		out[#out + 1] = { record = it:GetRecord():GetID(), count = c } i = i + c end
	return out end
function Refii.equipped(e) local x = E(e) if x == nil or not Carrying.IsAvailable(x) then return nil end
	local function r(w) if w == nil or Refii.handle(w) == nil then return nil end return w:GetRecord():GetID() end
	return r(Carrying.GetMeleeWeaponInAnySlot(x)), r(Carrying.GetRangedWeaponInAnySlot(x)) end
function Refii.equip_weapon(p, def) local x = P(p) if x == nil or not WeaponSetManager.IsAvailable(x) then return false end
	if Inventory.GetNumberOfItemsOfType(x, def) < 1 then return false end WeaponSetManager.EquipWeaponOfType(x, def) return true end
function Refii.use_item(p, def) local x = P(p) if x == nil or not Inventory.IsAvailable(x) then return false end
	if Inventory.GetNumberOfItemsOfType(x, def) < 1 then return false end return Refii._use_item(x, Inventory.GetItemOfType(x, def)) end
function Refii.perform_expression(p, expr) local x = P(p) if x == nil or not ExpressionPerformer.IsAvailable(x) then return false end
	if type(expr) == 'string' and not ExpressionPerformer.IsExpressionAvailable(x, expr) then return false end
	local r = GDB.GetRecord(expr) if r == nil then return false end ExpressionPerformer.PerformExpression(x, r)
	return ExpressionPerformer.IsPerformingExpression(x) end
function Refii.purity(e) local x = E(e) if x == nil or not Stats.IsAvailable(x) then return nil end return Stats.GetPrimal(x) end
function Refii.modify_purity(e, delta) local x = E(e) if x == nil or not Stats.IsAvailable(x) then return false end Stats.ModifyPrimal(x, delta) return true end
function Refii.opinion(villager, p) local v, h = E(villager), P(p) if v == nil or h == nil or not OpinionReaction.IsAvailable(v) then return nil end
	if OpinionReaction.GetCurrentPlayerOpinions(v, h) == nil then return nil end
	local o = {} for a = 0, 4 do o[a + 1] = OpinionReaction.GetCurrentOpinion(v, h, a) end return o[1], o[2], o[3], o[4], o[5] end
function Refii.modify_opinion(villager, hero, axis, delta) local v, h = E(villager), E(hero)
	if v == nil or h == nil or not OpinionReaction.IsAvailable(v) then return false end OpinionReaction.ModifyAxisValue(v, h, axis, delta) return true end
function Refii.perceived_renown(villager, p) local v = E(villager) if v == nil or not Villager.IsAvailable(v) then return nil end return Villager.GetPerceivedRenownOfHero(v) end
function Refii.discovered_crime(villager, p) local v = E(villager) if v == nil or not Villager.IsAvailable(v) then return nil end return Villager.HasHeroCommittedCrimeIHaveDiscovered(v) end
function Refii.forgive_crimes(villager, p) local v, h = E(villager), P(p) if v == nil or h == nil or not Villager.IsAvailable(v) then return nil end
	Villager.ForgiveHerosCrimes(v, h) return true end
function Refii.appearance(p) local x = P(p) if x == nil or not Stats.IsAvailable(x) then return nil end
	return { automatic = true, tan = Stats.GetTan(x), fat = Stats.GetFat(x), strength = Stats.GetStrength(x), skill = Stats.GetSkill(x),
		will = Stats.GetWill(x), morality = Stats.GetMorality(x), purity = Stats.GetPrimal(x), age = Age.IsAvailable(x) and Age.GetAge(x) or nil } end
function Refii.can_save(p) local x = P(p) if x == nil then return false end return Player.IsAbleToSaveGame(x) end
function Refii.save_game(p) local x = P(p) if x == nil or not Player.IsAbleToSaveGame(x) then return false end AutoSaveGame(true, true) return true end
function Refii.player_dog(p) local x = P(p) if x == nil then return nil end local d = GetDog(x) if d == nil then return nil end return Refii.handle(d) end
function Refii.key_taken(e, p) local c, x = E(e), P(p) if c == nil or x == nil or not OneTimeCollectable.IsAvailable(c) then return false end
	local s = OneTimeCollectable.GetCurrentStatus(c, x) return s == 1 or s == 2 end
function Refii._key_taken(e, p) return Refii.key_taken(e, p) and 1 or 0 end

-- Spells (gauntlets: five cast slots, one per charge level, each a main and a woven type).
function Refii.spell_in_slot(p, slot) local x = P(p) if x == nil then return -1 end
	return SpellManager.GetEntitySpellCastTableItem(x, slot), SpellManager.GetEntitySpellCastTableWeaveItem(x, slot) end
function Refii.spells_known(p) local x = P(p) if x == nil or not SpellManager.IsAvailable(x) then return nil end
	local out = {} for t = 0, 8 do for lv = 0, 4 do if SpellManager.IsEntitySpellTypePowerLevelPurchased(x, t, -1, lv) then out[#out + 1] = t break end end end
	return out end
function Refii.assign_spell(p, spell, opts) local x = P(p) if x == nil or not SpellManager.IsAvailable(x) then return false end
	local weave = (opts and opts.weave) or -1 for s = 0, 4 do SpellManager.SetEntitySpellCastTableItem(x, s, spell, weave) end
	return SpellManager.GetEntitySpellCastTableItem(x, 0) == spell end
function Refii._cast_spell(caster, ty, targeted, target, level, has_pos, px, py, pz, has_dir, dx, dy, dz)
	local c = E(caster) if c == nil or not SpellManager.IsAvailable(c) then return nil, nil end
	-- The preamble's flags are 1/0 (Engine_ModLua.cs Refii.cast_spell): 0 is true in Lua, so ON reads them.
	local pos = ON(has_pos) and CVector3(px, py, pz) or c:GetPosition()
	SpellManager.CreateScriptedSpellShot(c, ty, level or 0, pos, ON(targeted), target ~= nil and target ~= 0 and E(target) or nil,
		ON(has_dir) and CVector3(dx, dy, dz) or nil)
	return nil, nil end

-- Creatures under a mod's orders (the shipped scripts' own: CombatRegister, ScriptFunction, Follow,
-- Navigation, Faction).
function Refii.npc_attack(e, target) local x, t = E(e), E(target) if x == nil or t == nil or x == t then return false end
	CombatRegister:SetScriptTarget(x, t, true, false) return true end
function Refii.npc_flee(e, from) local x, f = E(e), E(from) if x == nil or f == nil or x == f or not Navigation.IsAvailable(x) then return false end
	Navigation.FleeFromEntity(x, f, ENavigationSpeed.NAV_SPEED_SPRINT, true) return true end
function Refii.npc_follow(e, target, radius) local x, t = E(e), E(target) if x == nil or t == nil or x == t or not Follow.IsAvailable(x) then return false end
	Follow.FollowEntity(x, t, (radius ~= nil and radius > 0) and radius or 2) return true end
function Refii.npc_go_to(e, px, py, pz) local x = E(e) if x == nil then return false end
	ScriptFunction.StartScriptControlledMovement(x, CVector3(px, py, pz), false) return true end
function Refii.npc_set_hostile(e, other, on) local x, t = E(e), E(other) if x == nil or t == nil or x == t or not Faction.IsAvailable(x) then return false end
	if on then Faction.AddTemporaryEntityRelationship(x, t, EFactionStatus.FACTION_STATUS_ENEMY) else Faction.RemoveTemporaryEntityRelationship(x, t) end
	return true end
function Refii.npc_stop(e) local x = E(e) if x == nil then return false end if Follow.IsAvailable(x) then Follow.StopFollowing(x) end
	ScriptFunction.StopScriptControlledMovement(x) CombatRegister:ClearScriptTarget(x) return true end

-- Targeting, camera, sound, weather, sessions.
function Refii.switch_target(p, left) local x = P(p) if x == nil or not Targeting.IsAvailable(x) or Targeting.GetTarget(x) == nil then return nil end
	Targeting.CheckForBetterTargetInDirection(x, left and true or false, 20)
	local t = Targeting.GetTarget(x) return t ~= nil and Refii.handle(t) or nil end
function Refii.focus(p) local x = P(p) if x == nil or not Targeting.IsAvailable(x) then return nil end
	local t = Targeting.GetTarget(x) if t == nil or not Refii.living(t) then return nil end return Refii.handle(t) end
function Refii.action_name(a) local t = Refii.action_type(a) if t == nil or t < 0 or EScriptableAction == nil then return nil end
	for k, v in pairs(EScriptableAction) do if v == t and k ~= "NUM_SCRIPTABLE_ACTIONS" then return k end end return nil end
Refii._music = {}
function Refii.play_music(name) local h = SoundTools.PlayMusic(name) if h == nil or h == 0 then return false end Refii._music[name] = h return true end
function Refii.stop_music(name) local h = Refii._music[name] if h == nil then return false end SoundTools.StopMusic(h) Refii._music[name] = nil return true end
function Refii.stop_sound(e, tag, fade)
	if e == 0 then return Refii._stop_point_sounds ~= nil and type(tag) == "string" and Refii._stop_point_sounds(tag, fade or 0) end
	local x = E(e) if x == nil or not Sound.IsAvailable(x) then return false end Sound.StopSoundCategoryPlaying(x, tag, fade or 0) return true
end
function Refii.set_weather(theme, weight, seconds) EnvironmentTheme.BlendToEnvironmentTheme(theme, weight or 1, seconds or 1) return true end
function Refii.coop_mode() if Network.IsInCouchGame() then return 1 end if Network.IsInLiveGame() then return Network.IsClient() and 3 or 2 end return 0 end
function Refii.split_screen() return false end
function Refii.menu_player() return 1 end
function Refii.player_for(e) local x = E(e) if x == nil then return nil end local p = Refii.player_of(x) if p > 0 then return p end
	if IsDog(x) then local m = Dog.GetMaster(x) if m ~= nil then p = Refii.player_of(m) if p > 0 then return p end end end
	local h = GetNearestHero(x) if h == nil then return nil end p = Refii.player_of(h) return p > 0 and p or nil end
-- MODE: CameraManager:FrameUpdate(player) (cameramanager.lua main.13) activates the first valid set's
-- best camera and keeps it as self.CurrentCamera; its mode is the key the override set
-- (OverridesCameraValidity) lists it under in CameraModes (ECameraMode), -1 for one it does not list.
--
-- HOLD (camera_override): CameraManager.SetCameraOverride(player, camera_enum, scope_enum, camera_data,
-- explicit_player) (main.0, 5 params): explicit_player falsy re-calls itself for GetLocalHero() AND
-- GetRemoteHero() (pc 4..23: `TEST 4 0 1; JMP -> 25` falls into the two calls), so a per-player hold
-- passes true. It writes Player.SetOverrideCameraMode(player, enum, scope, data) and ApplyOverride
-- (main.1), which keeps {CameraEnum, OverrideData} per half in GetOverriddenData(player) = Overrides
-- [GetIDFromEntity(player)] = {Normal, LookAt} (main.3/main.5; scope 0 ALWAYS writes both halves, 1
-- NORMAL the Normal one, 2 LOOK_AT the LookAt one: interactivecutsceneruleenum.lua). The override set
-- (main pc 96..103) is valid while CameraFunctions.HasOverride and picks CameraModes[CameraEnum]
-- WITHOUT asking IsValid (GetOverrideCamera, main.11), and it sits before the normal set in
-- CameraSetList (main pc 188..195: Debug, Mortar, Overrides, Multiplayer, Normal) — so a held mode
-- would beat the canned-action and cutscene cameras: the hold lets go while CannedActionCamera.IsValid
-- (hero) or CombatCannedActionCamera.IsValid() (their own validity, cannedactioncamera.lua main.0,
-- combatcannedactioncamera.lua main.0), while a FULL cutscene holds (Refii.in_cutscene(p, 1)), and,
-- with the aim flag, while RangedCombatCamera.IsValid(hero) (= CameraFunctions.IsRangedCombat,
-- rangedcombatcamera.lua main pc 5..8). Data keys are the cameras' own: FirstPersonCamera:Activate
-- reads OverriddenData.FOV or CameraValues.FOV.FPERSON (75; DEFAULT 65) (firstpersoncamera.lua main.2
-- pc 30..40); UpdateRotations clamps pitch to BoundAngleVert (or 0) +- BoundAngle when BoundAngleVert
-- is set, else VerticalAngularBound (main.9 pc 89..115); CameraBase:Activate reads BlendInSeconds and
-- Deactivate BlendOutSeconds (camerabase.lua main.17/main.21); ApplyOverride sets SupercedesCombat.
-- ECameraMode (cameramodeenum.lua): GAME 2, FIRST_PERSON 8, DEFAULT 0 — Fable 2's numbers.
--
-- NEVER SAVED: the save's CameraSave is CameraManager.GetSaveTable() (saveloadsystem.lua main.6 pc
-- 13..16, looked up at save time), whose Overrides IS the live CameraManager.Overrides (main.8). The
-- wrapper returns a copy whose halves our hold stands on are the default record.
Refii._camera_holds = Refii._camera_holds or {}
Refii._camera_applied = Refii._camera_applied or {}
Refii._camera_revision = Refii._camera_revision or 0
local CAMERA_HALVES = { { "Normal", 1 }, { "LookAt", 2 } }
local function CameraScope(bits)
	if bits == 3 then return EInteractiveCutsceneRuleScope.CUTSCENE_RULE_SCOPE_ALWAYS
	elseif bits == 1 then return EInteractiveCutsceneRuleScope.CUTSCENE_RULE_SCOPE_NORMAL end
	return EInteractiveCutsceneRuleScope.CUTSCENE_RULE_SCOPE_LOOK_AT
end
local function FIN(x) return type(x) == "number" and x - x == 0 end

-- The hold stated for hero's player, applied before the shipped FrameUpdate (Fable 2's C.CameraHold).
function Refii._camera_apply(hero)
	if next(Refii._camera_holds) == nil and next(Refii._camera_applied) == nil then return end
	local key = GetIDFromEntity(hero)
	if key == nil then return end
	local p = Refii.player_of(hero)
	local want = (p == 1 or p == 2) and Refii._camera_holds[p] or nil
	if want ~= nil and (want.mode == 0 or Refii.in_cutscene(p, 1)) then want = nil end
	local hold = Refii._camera_applied[key]
	if want == nil and hold == nil then return end
	if hold == nil then hold = { Written = {}, Bits = 0 } Refii._camera_applied[key] = hold end
	local wanted = want ~= nil
	if wanted and ((CannedActionCamera ~= nil and CannedActionCamera.IsValid(hero))
			or (CombatCannedActionCamera ~= nil and CombatCannedActionCamera.IsValid())) then wanted = false end
	if wanted and want.aim and RangedCombatCamera ~= nil and RangedCombatCamera.IsValid(hero) then wanted = false end
	if wanted and (hold.Data == nil or hold.Revision ~= want.revision) then
		local data = { SupercedesCombat = true }
		if want.fov > 0 then data.FOV = want.fov end
		if want.pitch > 0 then data.BoundAngleVert = 0 data.BoundAngle = want.pitch end
		if want.blend >= 0 then data.BlendInSeconds = want.blend data.BlendOutSeconds = want.blend end
		hold.Data, hold.Revision, hold.Mode, hold.Blend = data, want.revision, want.mode, want.blend
	end
	local record = CameraManager.GetOverriddenData(hero)
	local default = ECameraMode.CAMERA_MODE_DEFAULT
	local claim, release = 0, 0
	for _, half in ipairs(CAMERA_HALVES) do
		local name, bit = half[1], half[2]
		local slot = record[name]
		local written = hold.Written[name]
		local free = slot.CameraEnum == default
		local ours = written ~= nil and not free and slot.OverrideData == written
		if not free and not ours then
			hold.Written[name] = nil
		elseif wanted then
			if free or written ~= hold.Data then claim = claim + bit end
		elseif written ~= nil then
			release = release + bit
		end
	end
	if claim ~= 0 then
		CameraManager.SetCameraOverride(hero, hold.Mode, CameraScope(claim), hold.Data, true)
		for _, half in ipairs(CAMERA_HALVES) do
			if math.floor(claim / half[2]) % 2 == 1 then hold.Written[half[1]] = hold.Data end
		end
	end
	if release ~= 0 then
		local data = nil
		if hold.Blend ~= nil and hold.Blend >= 0 then data = { BlendInSeconds = hold.Blend } end
		CameraManager.SetCameraOverride(hero, default, CameraScope(release), data, true)
		for _, half in ipairs(CAMERA_HALVES) do
			if math.floor(release / half[2]) % 2 == 1 then hold.Written[half[1]] = nil end
		end
	end
	local bits = (hold.Written.Normal ~= nil and 1 or 0) + (hold.Written.LookAt ~= nil and 2 or 0)
	if bits ~= hold.Bits then
		hold.Bits = bits
		Refii._camera_held(hero, bits) -- natives.cpp: a held LookAt half is not a look-at cutscene
	end
	if want == nil and bits == 0 then Refii._camera_applied[key] = nil end
end

-- The save's copy: every half our hold stands on written as the default record, the live one untouched.
local function CameraSaveCopy(save)
	local overrides = type(save) == "table" and save.Overrides or nil
	if type(overrides) ~= "table" or next(Refii._camera_applied) == nil then return save end
	local out = {}
	for k, v in pairs(save) do out[k] = v end
	local copy = {}
	for id, record in pairs(overrides) do
		local hold = Refii._camera_applied[id]
		if hold ~= nil and type(record) == "table" then
			local entry = {}
			for field, value in pairs(record) do entry[field] = value end
			for _, half in ipairs(CAMERA_HALVES) do
				local name = half[1]
				local written = hold.Written[name]
				if written ~= nil and type(record[name]) == "table" and record[name].OverrideData == written then
					entry[name] = { CameraEnum = ECameraMode.CAMERA_MODE_DEFAULT }
				end
			end
			copy[id] = entry
		else
			copy[id] = record
		end
	end
	out.Overrides = copy
	return out
end

-- Wrapped once per CameraManager table: run again after the camera scripts (the runtime calls it there).
function Refii._wrap_camera()
	if type(CameraManager) ~= "table" then return end
	if type(CameraManager.GetSaveTable) == "function" and CameraManager.GetSaveTable ~= Refii._camera_save_wrapper then
		local shipped = CameraManager.GetSaveTable
		Refii._camera_save_wrapper = function(...)
			local save = shipped(...)
			local ok, copy = pcall(CameraSaveCopy, save)
			return ok and copy or save
		end
		CameraManager.GetSaveTable = Refii._camera_save_wrapper
	end
	if type(CameraManager.FrameUpdate) ~= "function" or CameraManager.FrameUpdate == Refii._camera_wrapper then return end
	local inner = CameraManager.FrameUpdate
	local function ModeOf(camera)
		if camera == nil or type(CameraManager.CameraSetList) ~= "table" then return -1 end
		for _, set in ipairs(CameraManager.CameraSetList) do
			if type(set) == "table" and set.OverridesCameraValidity and type(set.CameraModes) == "table" then
				for mode, listed in pairs(set.CameraModes) do if listed == camera then return mode end end
			end
		end
		return -1
	end
	Refii._camera_wrapper = function(self, player, a, b, c)
		if player ~= nil then pcall(Refii._camera_apply, player) end
		local r1, r2, r3 = inner(self, player, a, b, c)
		if player ~= nil and Refii._camera_stated ~= nil then
			pcall(Refii._camera_stated, player, ModeOf(self.CurrentCamera))
		end
		return r1, r2, r3
	end
	CameraManager.FrameUpdate = Refii._camera_wrapper
end
Refii._wrap_camera()
function Refii.host_serial(serial) return serial end
function Refii.local_serial(serial) return serial end

-- GODOT'S LANE. Godot posts the same command and query lines on both titles (Engine_RefiiHost.cs); Fable 2's
-- mod_commands.cpp serves them natively, Fable 3 hands every one its C++ does not serve itself to
-- Refii._command / Refii._query (mod_lua_hooks.cpp RunDispatcher), every word a string. A Godot script then
-- reaches exactly the Refii.* a Lua mod calls. A command's spec has one letter per word: n a number,
-- b a boolean sent as 1/0, s a word ('-' nil), a a number when it reads as one, else the text.
local unpack = table.unpack or unpack
local function Word(kind, w)
	if w == nil or w == "-" then return nil end
	if kind == "n" then return tonumber(w) end
	if kind == "b" then return w == "1" or w == "true" end
	if kind == "a" then return tonumber(w) or w end
	return w
end
Refii._commands = {
	add_item = { "add_item", "na" }, allow_action_interrupt = { "allow_action_interrupt", "n" },
	allow_action_interrupt_at = { "allow_action_interrupt_at", "nn" }, assign_spell = { "assign_spell", "nn" },
	cast_spell = { "cast_spell", "nnbnn" }, clear_speed_multiplier = { "clear_speed_multiplier", "n" },
	clear_status = { "clear_status", "nn" }, end_sprint = { "end_sprint", "n" }, entity_alpha = { "entity_alpha", "nn" },
	entity_drawable = { "entity_drawable", "nb" }, entity_saturation_mask = { "entity_saturation_mask", "nb" },
	entity_scale = { "entity_scale", "nn" }, entity_tint = { "entity_tint", "nnnnn" }, entity_untint = { "entity_untint", "n" },
	equip_weapon = { "equip_weapon", "na" }, forgive_crimes = { "forgive_crimes", "nn" },
	give_experience = { "give_experience", "nnn" }, give_gold = { "give_gold", "nn" }, knockback = { "knockback", "nnnnn" },
	lock_target = { "lock_target", "nn" }, modify_health = { "modify_health", "nnn" }, modify_morality = { "modify_morality", "nn" },
	modify_opinion = { "modify_opinion", "nnnn" }, modify_purity = { "modify_purity", "nn" }, modify_renown = { "modify_renown", "nn" },
	npc_attack = { "npc_attack", "nn" }, npc_flee = { "npc_flee", "nn" }, npc_follow = { "npc_follow", "nnn" },
	npc_go_to = { "npc_go_to", "nnnn" }, npc_set_hostile = { "npc_set_hostile", "nnb" }, npc_stop = { "npc_stop", "n" },
	perform_expression = { "perform_expression", "na" }, play_animation = { "play_animation", "nsnnn" },
	play_music = { "play_music", "s" }, play_sound = { "play_sound", "sns" }, play_sound_at = { "play_sound_at", "snnns" }, release_expression = { "release_expression", "n" }, release_lock = { "release_lock", "n" }, set_locomotion = { "set_locomotion", "nsn" }, morph_set = { "morph_set", "nsn" }, save_set = { "_save_set", "sn" },
	save_game = { "save_game", "n" }, set_facing = { "set_facing", "nnnn" }, set_health = { "set_health", "nn" },
	set_position = { "set_position", "nnnn" }, set_speed_multiplier = { "set_speed_multiplier", "nn" },
	set_time_of_day = { "set_time_of_day", "n" }, set_weather = { "set_weather", "snn" },
	spend_experience = { "spend_experience", "nnn" }, stagger = { "stagger", "n" }, stop_animation = { "stop_animation", "n" },
	stop_music = { "stop_music", "s" }, stop_sound = { "stop_sound", "nsn" }, switch_target = { "switch_target", "nb" },
	use_item = { "use_item", "na" },
}
local unknown = {}
-- save_set KEY INT: RefiiModSave.KEY in the game VM, carried with the save by the shared save preamble
-- (GetSaveTable / LoadScriptSave's RefiiMods key), as Fable 2's refii_mod_save_set_int writes it.
function Refii._save_set(key, value)
	if type(key) ~= "string" or not string.match(key, "^[%a_][%w_]*$") or type(value) ~= "number" then return false end
	RefiiModSave = RefiiModSave or {}
	RefiiModSave[key] = math.floor(value)
	return true
end
function Refii._command(op, ...)
	local c = Refii._commands[op]
	local f = c and Refii[c[1]]
	if f == nil then
		if not unknown[op] then unknown[op] = true Refii.log("Godot command '" .. tostring(op) .. "' has no Fable 3 implementation; dropped") end
		return
	end
	local w, a = { ... }, {}
	for i = 1, #c[2] do a[i] = Word(string.sub(c[2], i, i), w[i]) end
	f(unpack(a, 1, #c[2]))
end

-- A query's answer: name, value pairs, formatted as Fable 2's replies are (numbers, true/false, '..' text,
-- a record as '0x%08x'). nil is not ok.
-- 32-bit hex, 16 bits at a time: the console VM's %x goes through a signed 32-bit integer.
local function Hex32(n) n = n % 4294967296 return string.format("%04x%04x", math.floor(n / 65536), n % 65536) end
local function Record(v)
	if v == nil then return "'0x00000000'" end
	if type(v) == "number" then return "'0x" .. Hex32(v) .. "'" end
	local hex = string.lower(string.match(tostring(v), "(%x+)$") or "0")
	return "'0x" .. string.rep("0", 8 - math.min(8, #hex)) .. string.sub(hex, -8) .. "'"
end
local function Value(v)
	local t = type(v)
	if t == "boolean" then return v and "true" or "false" end
	if t == "number" then
		if v == math.floor(v) and math.abs(v) < 2147483648 then return string.format("%d", v) end
		return string.format("%.9g", v)
	end
	if t == "userdata" then return Record(v) end
	if v == nil then return "0" end
	return "'" .. string.gsub(tostring(v), "['\\\n\r]", "_") .. "'"
end
local RECORD = {}
local function Rec(v) return { RECORD, v } end
local function Fields(list)
	local out = {}
	local function Add(name, v)
		out[#out + 1] = name .. "=" .. ((type(v) == "table" and v[1] == RECORD) and Record(v[2]) or Value(v))
	end
	if list[1] ~= nil then
		for i = 1, #list, 2 do Add(list[i], list[i + 1]) end
	else
		local names = {}
		for name, _ in pairs(list) do names[#names + 1] = name end
		table.sort(names)
		for _, name in ipairs(names) do Add(name, list[name]) end
	end
	return table.concat(out, ",")
end
local N = tonumber
local function XYZ(fn) return function(e) local x, y, z = fn(N(e)) if x == nil then return nil end return { "x", x, "y", y, "z", z } end end
Refii._queries = {
	health = function(e) local c, m = Refii.health(N(e)) if c == nil then return nil end return { "current", c, "max", m } end,
	position = XYZ(Refii.position), facing = XYZ(Refii.facing), velocity = XYZ(Refii.velocity),
	gold = function(e) local g = Refii.gold(N(e)) if g == nil then return nil end return { "gold", g } end,
	item_count = function(e, d) return { "count", Refii.item_count(N(e), N(d) or d) or 0 } end,
	experience = function(e, pool) local v = Refii.experience(N(e), N(pool)) if v == nil then return nil end return { "amount", v } end,
	ability_level = function(e, t) local v = Refii.ability_level(N(e), N(t)) if v == nil then return nil end return { "level", v } end,
	relation = function(a, b) return { "relation", Refii.relation(N(a), N(b)) or 0 } end,
	alive = function(e) return { "alive", Refii.alive(N(e)) and true or false } end,
	locked_target = function(p) return { "target", Refii.locked_target(N(p)) or 0 } end,
	in_cutscene = function(p) local _, kinds = Refii.in_cutscene(N(p)) return { "kinds", kinds or 0 } end,
	camera_mode = function(p) local m, f = Refii.camera_mode(N(p)) return { "mode", m or -1, "fov", f or 0 } end,
	has_status = function(e, k) return { "has", Refii.has_status(N(e), N(k)) and true or false } end,
	speed_multiplier = function(p) local o, f = Refii.speed_multiplier(N(p)) if f == nil then return nil end return { "overridden", o and true or false, "factor", f } end,
	current_action = function(e) local a = Refii.current_action(N(e)) or 0
		return { "action", a, "elapsed", a ~= 0 and Refii.action_elapsed(a) or 0, "interruptible", a ~= 0 and Refii.action_interruptible(a) and true or false } end,
	action = function(a) a = N(a) return { "type", Refii.action_type(a) or -1, "name", Refii.action_name and Refii.action_name(a) or "",
		"elapsed", Refii.action_elapsed(a) or 0, "interruptible", Refii.action_interruptible(a) and true or false } end,
	living = function(e) return { "living", Refii.living(N(e)) and true or false } end,
	spell_in_slot = function(p, s) return { "type", Refii.spell_in_slot(N(p), N(s)) or -1 } end,
	host_serial = function(s) return { "serial", Refii.host_serial(N(s)) or 0 } end,
	local_serial = function(s) return { "serial", Refii.local_serial(N(s)) or 0 } end,
	time_of_day = function() return { "hours", Refii.time_of_day() or 0 } end,
	definition = function(e) local d = Refii.definition(N(e)) if d == nil then return nil end return { "definition", Rec(d) } end,
	name = function(e) local n = Refii.name(N(e)) if n == nil then return nil end return { "name", n } end,
	level_name = function() return { "level", Refii.level_name() or "" } end,
	hero = function(p) return { "hero", Refii.player_hero(N(p)) or 0 } end,
	player_of = function(e) return { "player", Refii.player_of(N(e)) or 0 } end,
	player_for = function(e) return { "player", Refii.player_for(N(e)) or 0 } end,
	world_here = function() return { "here", Refii.world_here() and true or false } end,
	can_save = function(p) return { "able", Refii.can_save(N(p)) and true or false } end,
	renown = function(e) local v = Refii.renown(N(e)) if v == nil then return nil end return { "renown", v } end,
	morality = function(e) local v = Refii.morality(N(e)) if v == nil then return nil end return { "value", v } end,
	purity = function(e) local v = Refii.purity(N(e)) if v == nil then return nil end return { "value", v } end,
	physique = function(e) local f, h = Refii.physique(N(e)) if f == nil then return nil end return { "fat", f, "height", h or 0 } end,
	age = function(e) local v = Refii.age(N(e)) if v == nil then return nil end return { "age", v } end,
	morph = function(p, m) local w = Refii.morph_get(N(p), m) return { "weight", w or 0, "registered", w ~= nil } end,
	-- frontend -> signed_in, saves, screen (Fable 2's reply); not ok before the game counted its saves.
	frontend = function()
		local saves = Refii.save_count and Refii.save_count() or -1
		if saves < 0 then return nil end
		return { "signed_in", Refii.signed_in(), "saves", saves, "screen", Refii.screen() }
	end,
	inventory_stack = function(e, i)
		local list, n = Refii.inventory(N(e)), N(i)
		local it = list ~= nil and n ~= nil and list[math.floor(n) + 1] or nil
		if it == nil then return nil end
		return { "definition", Rec(it.record), "count", it.count }
	end,
	-- lua_int EXPR... LO HI: the game Lua expression's integer value, if it is a number in [LO, HI].
	lua_int = function(...)
		local w = { ... }
		if #w < 3 then return nil end
		local lo, hi = N(w[#w - 1]), N(w[#w])
		local f = (loadstring or load)("return " .. table.concat(w, " ", 1, #w - 2))
		if f == nil or lo == nil or hi == nil then return nil end
		local ok, v = pcall(f)
		if not ok or type(v) ~= "number" or v ~= v or v < lo or v > hi then return nil end
		return { "value", math.floor(v) }
	end,
	spell_balance = function(e, t, power, targeted)
		local b = Refii.spell_balance(N(e), N(t), N(power) or 0, targeted ~= "0" and targeted ~= "false")
		if b == nil then return nil end
		return { "status_seconds", b.status_seconds or -1, "status_spread", b.status_spread or -1,
			"duration", b.duration or -1, "swords", b.swords or -1 }
	end,
	equipped = function(e) local m, r = Refii.equipped(N(e)) return { "melee", Rec(m), "ranged", Rec(r) } end,
	inventory = function(p, offset)
		local list = Refii.inventory(Refii.player_hero(N(p))) if list == nil then return nil end
		offset = math.max(0, N(offset) or 0)
		local out, count = {}, 0
		for i = offset + 1, math.min(#list, offset + 40) do
			count = count + 1
			local it = list[i]
			out[#out + 1] = "d" .. count out[#out + 1] = Rec(it.record) out[#out + 1] = "n" .. count out[#out + 1] = it.count
			out[#out + 1] = "c" .. count out[#out + 1] = 0 out[#out + 1] = "v" .. count out[#out + 1] = 0 out[#out + 1] = "u" .. count out[#out + 1] = 0
		end
		local head = { "total", #list, "offset", offset, "next", offset + count < #list and offset + count or #list, "count", count }
		for _, v in ipairs(out) do head[#head + 1] = v end
		return head
	end,
	spells_known = function(p) local list = Refii.spells_known(N(p)) if list == nil then return nil end
		local mask = 0 for _, t in ipairs(list) do mask = mask + 2 ^ t end return { "mask", mask, "count", #list } end,
	sprinting = function(p) return { "sprinting", Refii.sprinting(N(p)) and true or false } end,
	record_string = function(d, f) local v = Refii.record_string(N(d) or d, f) if v == nil then return nil end return { "value", v } end,
	record_int = function(d, f) local v = Refii.record_int(N(d) or d, f) if v == nil then return nil end return { "value", v } end,
	record_number = function(d, f) local v = Refii.record_number(N(d) or d, f) if v == nil then return nil end return { "value", v } end,
	record_bool = function(d, f) local v = Refii.record_bool(N(d) or d, f) if v == nil then return nil end return { "value", v and true or false } end,
	record_exists = function(d) return { "exists", Refii.record_exists(N(d) or d) and true or false } end,
	record_ref = function(d, f) local v = Refii.record_ref(N(d) or d, f) if v == nil then return nil end return { "value", Rec(v) } end,
	creature_tier = function(e) return { "tier", Refii.creature_tier(N(e)) or -1 } end,
	near = function(x, y, z, r, creatures, offset)
		local list = Refii.near(N(x), N(y), N(z), N(r), creatures == "1")
		offset = math.min(math.max(0, N(offset) or 0), #list)
		local count = math.min(#list - offset, 100)
		local out = { "total", #list, "offset", offset, "count", count }
		for i = 1, count do out[#out + 1] = "e" .. i out[#out + 1] = list[offset + i] end
		return out
	end,
	line_of_sight = function(a, b, c, d, e, f) return { "clear", Refii.line_of_sight(N(a), N(b), N(c), N(d), N(e), N(f)) and true or false } end,
	spawn = function(def, x, y, z) local h = Refii.spawn(N(def) or def, N(x), N(y), N(z)) if h == nil then return nil end return { "entity", h } end,
	local_hero = function() return { "hero", Refii.local_hero() or 0 } end,
	coop_mode = function() return { "mode", Refii.coop_mode() or 0 } end,
	menu_player = function() return { "player", Refii.menu_player() or 1 } end,
	coop_state = function() return { "local_hero", Refii.local_hero() or 0, "coop_mode", Refii.coop_mode() or 0,
		"menu_player", Refii.menu_player() or 1, "split_screen", Refii.split_screen() and true or false } end,
	player_value = function(p, key, fallback) local v = Refii.player_value(N(p), key)
		if v == nil and fallback == nil then return nil end return { "value", v or N(fallback) or 0, "has", v ~= nil } end,
	player_here = function(p) return { "here", Refii.player_here(N(p)) and true or false } end,
	player_dog = function(p) return { "dog", Refii.player_dog(N(p)) or 0 } end,
	focus = function(p) return { "entity", Refii.focus(N(p)) or 0 } end,
	opinion = function(v, p) local a, b, c, d, e = Refii.opinion(N(v), N(p)) if a == nil then return nil end
		return { "love", a, "fear", b, "attractiveness", c, "morality", d, "respect", e } end,
	discovered_crime = function(v, p) return { "discovered", Refii.discovered_crime(N(v), N(p)) and true or false } end,
	perceived_renown = function(v, p) local r = Refii.perceived_renown(N(v), N(p)) if r == nil then return nil end return { "renown", r } end,
	uid = function(e) local u = Refii.uid(N(e)) if u == nil then return nil end
		local hi, lo = 0, 0 -- the decimal UID text into two 32-bit halves, exactly (a double holds each step)
		for d in string.gmatch(u, "%d") do
			lo = lo * 10 + tonumber(d)
			local carry = math.floor(lo / 4294967296)
			lo = lo - carry * 4294967296
			hi = (hi * 10 + carry) % 4294967296
		end
		return { "uid", "0x" .. Hex32(hi) .. Hex32(lo) } end,
	appearance = function(p) local a = Refii.appearance(N(p)) if a == nil then return nil end
		return { "automatic", a.automatic and true or false, "tan", a.tan or 0, "fat", a.fat or 0, "height", a.height or 0,
			"strength", a.strength or 0, "skill", a.skill or 0, "will", a.will or 0, "morality", a.morality or 0, "purity", a.purity or 0, "age", a.age or 0 } end,
}
function Refii._query(id, verb, ...)
	local q = Refii._queries[verb]
	if q == nil then
		Refii._query_reply(id, false, "error='" .. tostring(verb) .. " is not available in Fable 3 yet'")
		return
	end
	local ok, list = pcall(q, ...)
	if not ok then
		Refii._query_reply(id, false, "error='" .. string.gsub(tostring(list), "['\\\n\r]", "_") .. "'")
	elseif list == nil then
		Refii._query_reply(id, false, "")
	else
		Refii._query_reply(id, true, Fields(list))
	end
end

-- Refii._camera_override(p, mode, fov, pitch_limit, blend, flags) -> stated: the native the shared
-- preamble's Refii.camera_override(p, mode, {fov, pitch_limit, blend, aim}) calls (Engine_ModLua.cs);
-- on Fable 3 the hold lives here (the VM is the session: a fresh VM starts with none, as Fable 2's
-- ModCameraClearHolds). Fable 2's rules (refii_mod_camera_override): mode 0, 2 or 8; fov clamped to
-- 179, pitch to 90, 0 or less the camera's own; blend below 0 the camera's own; flags 1 the aim.
function Refii._camera_override(p, mode, fov, pitch, blend, flags)
	p, mode = tonumber(p), tonumber(mode) or 0
	fov, pitch, blend, flags = tonumber(fov) or 0, tonumber(pitch) or 0, tonumber(blend) or -1, tonumber(flags) or 0
	if (p ~= 1 and p ~= 2) or (mode ~= 0 and mode ~= 2 and mode ~= 8) or not (FIN(fov) and FIN(pitch) and FIN(blend)) then
		return false
	end
	Refii._camera_revision = Refii._camera_revision + 1
	Refii._camera_holds[p] = { mode = mode, fov = fov > 0 and math.min(fov, 179) or 0, pitch = pitch > 0 and math.min(pitch, 90) or 0,
		blend = blend >= 0 and blend or -1, aim = math.floor(flags) % 2 == 1, revision = Refii._camera_revision }
	return true
end

-- Refii.fov(p) -> degrees of the camera this machine draws, 0 with none or for a player played on the
-- other machine (Fable 3 draws one camera per machine; couch co-op shares it).
--   The camera ENTITY is the active camera table's .Camera: CameraBase:Activate(player_entity, camera,
--   previous) stores `self.Camera = camera` and calls Camera.GetFOV(self.Camera) (camerabase.lua main.17
--   pc 0, 15..19). NOT CameraManager.GetCurrentCamera(): its glue (TU3 0x8232B888 / disc 0x82324F08)
--   pushes the Lua TABLE CameraManager.ActivateCamera stored (Lua_CameraManager_ActivateCamera TU3
--   0x82321200 / disc 0x8231A308 takes arg 1 only as tag 5/12 "CameraTable"), and that table in
--   Camera.GetFOV is the crash: the glue TU3 0x825C9230 / disc 0x8285EC20 reads arg 1 as tag 7 (+16) or
--   tag 2, else a null `*v9` -> lhScriptEntityRef_Resolve(*v9).
--   Camera.GetFOV's upvalue (sub_826A6FE0 TU3 / disc 0x8267D388: `*v17 = a3; v17[1] = sub_826A65E8`)
--   runs sub_826A65E8(wrapper, entity): 0.0 unless live (+0x94 & 0x40) with component 108, then
--   Lua_Camera_GetFOV(component) TU3 0x826A0010 / disc 0x82676658 = `*(float*)(c+24) * 57.295776`.
--   Refii.handle(e) checks the userdata carries the Entity metatable (EntityArgument) before the glue.
-- spell_balance(caster, type, power [, targeted]) -> {status_seconds, status_spread, duration, swords}:
-- one power (0..4) of a spell for the caster's balance record, read by the game's own read as a new spell
-- is fed (mod_lua_hooks.cpp _spell_balance). Fable 3's spell types; targeted (default true) or area, its
-- records being separate. A field the class does not carry is nil; nil with no spell manager.
function Refii.spell_balance(caster, type, power, targeted)
	if Refii._spell_balance == nil then return nil end
	local x = E(caster)
	if x == nil then return nil end
	local seconds, spread, duration, swords = Refii._spell_balance(x, type or -1, power or 0, targeted ~= false)
	if seconds == false then return nil end
	return { status_seconds = seconds, status_spread = spread, duration = duration, swords = swords }
end
function Refii.fov(p)
	-- Fable 2's contract: the horizontal 4:3 angle of p's live camera in degrees, 0 with none. Fable 3
	-- renders one camera per machine, stated to View_SetRenderCamera each frame (_view_fov): a couch
	-- pair shares it, a player played on the other machine has none here.
	if not Refii.player_here(p or 1) or Refii.player_hero(p or 1) == nil or Refii._view_fov == nil then return 0 end
	return Refii._view_fov()
end

-- =================================================================================================
-- C. EXPRESSIONS (append)
-- =================================================================================================
-- A record ID (light userdata) as "0x…" text, as Refii.record_id writes one.
local function IdHex(id)
	if id == nil then return nil end
	local hex = string.match(tostring(id), "(%x+)$")
	if hex == nil or #hex > 8 then return nil end
	return "0x" .. string.lower(string.rep("0", 8 - #hex) .. hex)
end

-- Refii.expressions(p) -> { {record = "0x…", id = <record ID>, extendable = bool}, … }, or nil without
-- a performer. The performer's known list (component 177, vector +0x8C..+0x90, 20-byte entries {record
-- ID, record, extendable, ..}: natives.cpp Refii._expression). `id` is the record ID light userdata,
-- what Refii.perform_expression's GDB.GetRecord takes (tag 2); `record` the same as "0x…" text. Fable 3
-- keeps no use count (Fable 2's `uses`).
function Refii.expressions(p)
	local out = {}
	for i = 0, 511 do
		local id, extendable = Refii._expression(p or 1, i)
		if id == false then return nil end -- no hero, or no performer (component 177)
		if id == nil then break end
		out[#out + 1] = { record = IdHex(id), id = id, extendable = extendable }
	end
	return out
end

-- =================================================================================================
-- D. WORLD (append)
-- =================================================================================================
-- Refii.add_deed(villager, hero, deed) -> bool: the deed message Fable 3's own debug post sends —
-- Debug.PostGenericDeed (opinionsdebug.lua main.26): MessageEvents.PostMessage({ type =
-- EMessageEventType.MESSAGE_EVENT_DEED_PERSON, from = GetLocalHero(), to = nil, extra_data =
-- GDB.GetRecord("Generic_Scary"):GetID(), pos = GetLocalHero():GetPosition() }), and SexFunctions'
-- (sexfunctions.lua pc 8..27) with `to` the person. Lua_MessageEvents_PostMessage (TU3 0x823B5938 /
-- disc 0x8244FAC8, changed; the same seven keys) reads "type" (number, or the enum name), "from"/"to" (FableType 16, Entity),
-- "extra_data" (tag 2 light userdata, or a number), "pos" (FableType 1, CVector3). Villagers react through
-- BehaviourReactToDeed (record = GDB.GetRecord(message:GetExtraDataAsID()), the record's MaxReactDistance
-- to GetEntitySentBy(), CanChildrenReactToThis, ...: behaviourreacttodeed.lua main.0), so the record's
-- own rules apply, as on Fable 2. deed is the Deed record's NAME ("Generic_Scary") or its ID (Fable 3's
-- record IDs are not FNV-1 of the name, so Fable 2's "0x…" hash form has no meaning here).
function Refii.add_deed(villager, hero, deed)
	local v, h = E(villager), E(hero)
	if v == nil or h == nil then return false end
	if type(deed) == "string" and not GDB.RecordExists(deed) then return false end
	if type(deed) ~= "string" and type(deed) ~= "userdata" then return false end
	local r = GDB.GetRecord(deed)
	if r == nil or r:IsNull() then return false end
	MessageEvents.PostMessage({ type = EMessageEventType.MESSAGE_EVENT_DEED_PERSON, from = h, to = v,
		extra_data = r:GetID(), pos = h:GetPosition() })
	return true
end
-- The command word Godot posts (Engine_RefiiHost.add_deed: Arg(deed), a string as a quoted Lua literal).
function Refii._add_deed_word(villager, hero, word)
	if type(word) ~= "string" then return false end
	local name = string.match(word, '^"(.*)"$') or word
	if name == "" or tonumber(name) ~= nil then return false end
	return Refii.add_deed(tonumber(villager), tonumber(hero), name)
end

-- =================================================================================================
-- E. PLAYERS (append)
-- =================================================================================================
-- "The hero" in Fable 3's scripts is GetLocalHero() and the partner GetRemoteHero():
-- Lua_Global_GetLocalHero (TU3 0x821FB370 / disc 0x8236D410) reads the world's +0x7C holder and
-- Lua_Global_GetRemoteHero (TU3 0x821DFE98 / disc 0x821E02D0) +0x84 — the slots Refii.player_hero(1)
-- and (2) read (mod_lua_hooks.cpp kWorldHeroOne / kWorldHeroTwo); both natives through the glue TU3
-- 0x823A1220 / disc 0x82324C50 (no arguments, pushes the entity). So acting as player p is those two
-- answering p's hero and the other's, as Fable 2's run_as answers GetPlayerHero / GetPlayerHenchman.
-- The scope is per coroutine (a yield inside fn keeps it; the main thread is keyed by the table itself).
Refii._acting = Refii._acting or setmetatable({}, { __mode = "k" })
local function ActingKey() return coroutine.running() or Refii._acting end
local function WrapHeroes()
	if Refii._shipped_heroes ~= nil then return true end
	if type(GetLocalHero) ~= "function" or type(GetRemoteHero) ~= "function" then return false end
	local loc, rem = GetLocalHero, GetRemoteHero
	Refii._shipped_heroes = { loc, rem }
	GetLocalHero = function() if Refii._acting[ActingKey()] == 2 then return rem() end return loc() end
	GetRemoteHero = function() if Refii._acting[ActingKey()] == 2 then return loc() end return rem() end
	return true
end
local function Leave(key, before, ...) Refii._acting[key] = before return ... end

-- Refii.acting_player() -> 1 or 2: who GetLocalHero() means right now.
function Refii.acting_player()
	local p = Refii._acting[ActingKey()]
	if p ~= nil then return p end
	local hero = GetLocalHero()
	local q = hero ~= nil and Refii.player_of(hero) or 0
	return q == 2 and 2 or 1
end

-- Refii.run_as_player(p, fn, ...): fn with GetLocalHero() as player p's hero and GetRemoteHero() as the
-- other; fn as it stands for any other p or with no player p. Inside a coroutine the scope is left
-- when fn returns (a yield keeps it for that coroutine alone; an error ends the coroutine).
function Refii.run_as_player(p, fn, ...)
	if type(fn) ~= "function" then return nil end
	if (p ~= 1 and p ~= 2) or Refii.player_hero(p) == nil or not WrapHeroes() then return fn(...) end
	local key = ActingKey()
	local before = Refii._acting[key]
	Refii._acting[key] = p
	if key ~= Refii._acting then return Leave(key, before, fn(...)) end
	-- The main thread: the scope restored on an error too (at most eight results, no select/unpack:
	-- the shipped scripts never use them, so the VM's base library is not relied on for them).
	local ok, a, b, c, d, e, f, g, h = pcall(fn, ...)
	Refii._acting[key] = before
	if not ok then error(a, 0) end
	return a, b, c, d, e, f, g, h
end

-- Refii.show_to_player(p, name, ...): the function a global path names ("GUI.DisplayMessageBox", any)
-- run as player p. Fable 3 has no partner lane in its mod runtime, so a player played on the other
-- machine answers nil (Fable 2 routes it there); played here, it runs here as p (couch co-op's one
-- screen is both players').
function Refii.show_to_player(p, name, ...)
	if (p ~= 1 and p ~= 2) or type(name) ~= "string" or Refii.player_hero(p) == nil or not Refii.player_here(p) then return nil end
	local fn, at = _G, 1
	while at <= #name do
		local dot = string.find(name, ".", at, true)
		local part = string.sub(name, at, (dot or #name + 1) - 1)
		if type(fn) ~= "table" or part == "" then return nil end
		fn = fn[part]
		at = (dot or #name) + 1
	end
	if type(fn) ~= "function" then return nil end
	return Refii.run_as_player(p, fn, ...)
end

-- =================================================================================================
-- F. (natives only) Refii.player_value_hash / player_save_hash, Refii.morph_id, Refii.locomotion,
--    Refii.wheel_switches_target: C++, natives.cpp.
-- =================================================================================================

-- =================================================================================================
-- G. THE DISPATCHER'S ENTRIES (merge into Refii._commands / Refii._queries)
-- =================================================================================================
-- Commands, as Engine_RefiiHost posts them:
--   camera_override P MODE FOV PITCH BLEND FLAGS   (Post line 1207: all six words always)
--   wheel_switches_target E ON                      (line 2208: ON 1 / 0)
--   add_deed V H DEED                               (line 2458: DEED = Arg(variant), a quoted literal)
--   (player_save_hash posts player_save_text, which RunHostAnswer already serves)
Refii._commands = Refii._commands or {}
Refii._commands.camera_override = { "_camera_override", "nnnnnn" }
Refii._commands.wheel_switches_target = { "wheel_switches_target", "nb" }
Refii._commands.add_deed = { "_add_deed_word", "nns" }

-- Queries, Fable 2's field names (mod_commands.cpp header):
--   camera_mode P -> mode, fov · expressions P -> count, e1..eN ('0x…') (x1..xN extendable 0/1; Fable 3
--   has no u1..uN) · locomotion P KEY -> value · player_text P KEY -> text, hash, has
Refii._queries = Refii._queries or {}
Refii._queries.camera_mode = function(p)
	p = tonumber(p) or 0
	return { mode = Refii.camera_mode(p), fov = Refii.fov(p) }
end
Refii._queries.expressions = function(p)
	local list = Refii.expressions(tonumber(p) or 0)
	if list == nil then return nil end
	local out = { count = #list }
	for i, x in ipairs(list) do out["e" .. i] = x.record or "" out["x" .. i] = x.extendable and 1 or 0 end
	return out
end
Refii._queries.locomotion = function(p, key)
	local v = Refii.locomotion(tonumber(p) or 0, key)
	if v == nil then return nil end
	return { value = v }
end
Refii._queries.player_text = function(p, key)
	p = tonumber(p) or 0
	local text = Refii.player_value_text(p, key)
	-- mod_commands.cpp Quote: a quote, backslash, comma or line break as '_'
	local quoted = text ~= nil and string.gsub(text, "['\\,\r\n]", "_") or ""
	return { text = quoted, hash = Refii.player_value_hash(p, key) or "", has = text ~= nil }
end

-- ---------------------------------------------------------------------------------------------
-- SPELL_* (Fable 3's ESpellType, scripts/miscellaneous/spelltypes.lua): SPELL_LIGHTNING 0,
-- SPELL_FIREBALL 1, SPELL_SLOW_TIME 2, SPELL_SWORDS 3, SPELL_VORTEX 4, SPELL_CHAOS 5,
-- SPELL_FORCE_PUSH 6, SPELL_DEAD_RISING 7, SPELL_ICE_STORM 8, NUMBER_OF_SPELLS 9. 0..7 are Fable 2's
-- numbers (the shared preamble's SPELL_LIGHTNING .. SPELL_RAISE_DEAD, ModApi.Constants, hold on
-- Fable 3 unchanged: BLADES = SWORDS 3, RAISE_DEAD = DEAD_RISING 7). The binary agrees on the range:
-- CSpellRegistry_Create switches on the parameters' type, `lwz r11,0(r31); cmplwi cr6,r11,8` (TU3
-- 0x8268b7a0 / disc 0x82662398), anything above 8 takes the default. Fable 3 only:
Refii.SPELL_ICE_STORM = 8

-- ---------------------------------------------------------------------------------------------
-- combat_state(p) -> midst, close_to_combat, in_any_combat: player p's hero's own player component
-- (37), which Fable 3 keeps per hero, so no per-hero bookkeeping is needed (Fable 2's global is one).
--   Player.GetMidstOfCombatCounter  glue 0x825C9230 (disc 0x8285EC20 exact): arg 1 the Entity
--     (lhScriptEntityRef_Resolve of base[0]), pushes a NUMBER (tag 3). Native 0x821CB3D0 (disc
--     0x821CB378 exact): `return *(float *)(a1 + 148)` (component +0x94).
--   Player.IsCloseToCombat  glue 0x82957C78 (disc 0x82AD5DA8 exact): arg 1 the Entity, pushes a
--     BOOLEAN (tag 1). Native 0x82442CB0 (disc 0x82424A58 exact): true when +152 (+0x98) is set, or
--     the tick now less +156 is within 1.5 s of ticks (dbl_832DF4D0 * 1.5): the game's own answer,
--     which holds "close" 1.5 s after the flag drops.
--   Player.IsInAnyCombat  glue 0x82957C78, native 0x82442D20 (disc 0x82424AC8 exact):
--     `return *(u8 *)(a1 + 153)` (+0x99).
--   The writer, Player.SetMidstOfCombatCounter 0x82173020: +148 = f1, +152 = close, +153 = any,
--   `*(result + 4)` the owner entity — a component member, one per hero.
function Refii.combat_state(p) local x = P(p == 2 and 2 or 1) if x == nil or not Player.IsAvailable(x) then return 0, false, false end
	return Player.GetMidstOfCombatCounter(x), Player.IsCloseToCombat(x) and true or false, Player.IsInAnyCombat(x) and true or false end

-- ---------------------------------------------------------------------------------------------
-- spell_parts(serial) -> { spell, type, targeted, level, caster, caster_player, vortex, anchor,
-- swords = { .. }, origin = { x, y, z } or nil, exploded }, nil for a cast the factory never saw.
-- C++ (Refii._spell_parts, natives.cpp) answers from the spell seams' records; the origin falls back to
-- the caster's position through the game's own GetPosition (Refii.position) when no part states one.
-- Fable 3: anchor is the vortex's entity; a fireball's shot entity is not stated (anchor nil).
function Refii.spell_parts(serial)
	local r = { Refii._spell_parts(serial) }
	if r[1] == nil then return nil end
	local t = { type = r[1], targeted = r[2], level = r[3], caster = r[4], caster_player = r[5], spell = r[6],
		vortex = r[7], anchor = r[8], exploded = r[9], swords = {} }
	for i = 14, 21 do if r[i] ~= nil then t.swords[#t.swords + 1] = r[i] end end
	if r[10] then t.origin = { r[11], r[12], r[13] }
	elseif t.caster ~= nil then local x, y, z = Refii.position(t.caster) if x ~= nil then t.origin = { x, y, z } end end
	return t end

-- vortex_held(serial) -> { e, .. } in the vortex's own list order, nil when no live vortex carries serial.
function Refii.vortex_held(serial)
	local r = { Refii._vortex_held(serial) }
	if r[1] == nil then return nil end
	local out = {} for i = 2, r[1] + 1 do if r[i] ~= nil then out[#out + 1] = r[i] end end
	return out end

-- spell_serial(spell) is the C++ native itself (Refii.spell_serial, natives.cpp).

-- ---------------------------------------------------------------------------------------------
-- charge_spell(p, type, held): spell `type` (0..8) through player p's own magic charge. The tiers the
-- hero has the spell on are read with the game's own purchase test; the lend, the button and the
-- restore are C++ (Refii._charge_lend: the slot writer with its send flag clear, so no spell_pick and
-- nothing sent; the slots come back once the charge is over).
--   SpellManager.IsEntitySpellTypePowerLevelPurchased  glue 0x8263BF38 (disc 0x82613768 exact):
--     arg 1 the Entity, arg 2 type, arg 3 weave, arg 4 level (each lua_tonumber, sub_82233CF8(a1, n)),
--     calls native(entity, type, weave, level), pushes a BOOLEAN. Native 0x82635BF0 (disc 0x8260D5F8
--     exact): the manager (sub_82296448), then sub_8268A508(mgr + 148, type, weave, level), which for
--     weave -1 answers `*(u8 *)(mgr + 148 + 4 + 5 * type + level) != 0`.
-- Tier s is lent when the spell is purchased at power level s; the tiers above the first one it is not
-- are emptied, so the charge stops where the spell does (Fable 2's rule). False: not purchased on the
-- first tier, no hero, not played on this machine, or (held false) no lend out.
function Refii.charge_spell(p, ty, held)
	if not ON(held) then return Refii._charge_lend(p, -1, 0, 0) end
	local x = P(p == 2 and 2 or 1)
	if x == nil or not SpellManager.IsAvailable(x) or type(ty) ~= 'number' or ty < 0 or ty > 8 then return false end
	local tiers = 0
	for s = 0, 4 do if SpellManager.IsEntitySpellTypePowerLevelPurchased(x, ty, -1, s) then tiers = s + 1 else break end end
	if tiers == 0 then return false end
	return Refii._charge_lend(p, ty, tiers, 1) end

-- ---------------------------------------------------------------------------------------------
-- parry(p) / set_parry(p, window, wedge): NOT ON FABLE 3. Fable 2's parry reads InputWindowSecondsForParry
-- and MeleeParryWedgeAngle from each hero's copy of HeroCombatBalanceParams; no Fable 3 code reads
-- either name (neither string exists in the TU3 or the disc binary). These answer as Fable 2 does when
-- the hero has no such parameters: nil / false.
function Refii.parry(p) return nil end
function Refii.set_parry(p, window, wedge) return false end

-- ---------------------------------------------------------------------------------------------
-- Godot's lines (Engine_RefiiHost.cs Post / query) through the dispatcher. Command words are strings;
-- spec n number, b boolean sent 1/0. attack_mode P MODE stays C++'s (RunHostAnswer).
Refii._commands = Refii._commands or {}
Refii._commands.charge_spell = { 'charge_spell', 'nnb' }      -- charge_spell P TYPE HELD
Refii._commands.apply_status = { 'apply_status', 'nnnnn' }    -- apply_status E KIND SECONDS LEVEL SOURCE
Refii._commands.set_parry = { 'set_parry', 'nnn' }            -- set_parry P WINDOW WEDGE

-- Queries: the field names of Fable 2's mod_commands.cpp replies; nil is ok=false.
Refii._queries = Refii._queries or {}
Refii._queries.spell_parts = function(serial)
	local t = Refii.spell_parts(tonumber(serial))
	if t == nil then return nil end
	local o = t.origin
	local r = { spell = t.spell or 0, type = t.type, targeted = t.targeted and true or false, level = t.level,
		caster = t.caster or 0, caster_player = t.caster_player or 0, vortex = t.vortex or 0, anchor = t.anchor or 0,
		sword_count = #t.swords, x = o and o[1] or 0, y = o and o[2] or 0, z = o and o[3] or 0,
		has_origin = o ~= nil, exploded = t.exploded and true or false }
	for i = 1, 8 do r['s' .. i] = t.swords[i] or 0 end
	return r end
Refii._queries.vortex_held = function(serial)
	local held = Refii.vortex_held(tonumber(serial))
	if held == nil then return nil end
	local r = { count = #held } for i = 1, 16 do r['v' .. i] = held[i] or 0 end
	return r end
-- Fable 2's reply carries the three as booleans (mod_commands.cpp: midst=%s from `select(1, ..) and 1 or 0`);
-- midst is the counter above zero here.
Refii._queries.combat_state = function(p)
	p = tonumber(p) if p ~= 1 and p ~= 2 then return nil end
	local midst, close, any = Refii.combat_state(p)
	return { midst = (midst or 0) > 0, close_to_combat = close and true or false, in_any_combat = any and true or false } end
Refii._queries.parry = function(p) return nil end

-- 4. MOD ABILITIES ON FABLE 3 (mod_ability, 91). Fable 2's engine (Fable2/src/vfs/src/hooks/
-- mod_abilities.cpp) prices a level from the game's own experience table (costLike, 1..14) split
-- over Strength/Skill/Will pools: none of that exists in Fable 3, whose progression is bought with
-- GUILD SEALS. Here a declared ability's level N costs `seals[N]` guild seals (a new abilities.json
-- field, carried as the declaration's 10th word); an ability with no seal price for the next level
-- cannot be bought on Fable 3. The level is the same saved player value (levelKey), the event the
-- same (experience = the seals spent or given back), the stated .cost/.refund/.afford the same keys.
-- A refund ("half") gives half the given-up level's seals back through ModifyGuildSeals.
-- Godot declares them (ability_clear / ability_declare, godot.cs); a mod's own menu buys them through
-- Refii.ability_buy / ability_refund (Fable 3 has no abilities book; the Road to Rule is the game's).
Refii._abilities = Refii._abilities or {}
local function AbilityLevel(a, p)
	local v = math.floor(Refii.player_value(p, a.key) or 0)
	if v < 0 then return 0 end
	if v > a.max then return a.max end
	return v
end
local function SealPrice(a, level) local s = a.seals[level] if s == nil or s <= 0 then return nil end return math.floor(s) end
local function SealsHeld(x) if x == nil or not Stats.IsAvailable(x) then return 0 end return Stats.GetNumberOfGuildSeals(x) end
local function AbilityText(v) return string.gsub(tostring(v or ""), "['\\\n\r]", "_") end
local function AbilityCount() local n = 0 for _, _ in pairs(Refii._abilities) do n = n + 1 end return n end
-- How many the engine holds, for Godot: a load rebuilds the VM and empties it (Fable3Title.PumpMods declares again).
local function StateAbilityCount() if Refii.publish ~= nil then Refii.publish("refii.abilities", AbilityCount()) end end
function Refii._ability_clear() Refii._abilities = {} StateAbilityCount() end
-- ability_declare NAME MOD MAX COSTLIKE COSTLIKE_SETTING LEVELKEY POOLS REFUND LEGACY [SEALS]
function Refii._ability_declare(name, mod, max, costlike, costlike_setting, key, pools, refund, legacy, seals)
	if name == nil or key == nil or max == nil or max < 1 then return end
	local prices = {}
	if seals ~= nil then for s in string.gmatch(tostring(seals), "[^,]+") do prices[#prices + 1] = tonumber(s) end end
	Refii._abilities[name] = { name = name, mod = mod or "", max = max, key = key, half = refund == "half", seals = prices }
	StateAbilityCount()
end
local function Purchase(p, name, refund)
	local a, x = Refii._abilities[name], P(p)
	if a == nil or x == nil or not Refii.player_here(p) then return false end
	local level = AbilityLevel(a, p)
	local price
	if refund then
		if level <= 0 or not a.half then return false end
		price = SealPrice(a, level) if price == nil then return false end
		price = math.floor(price / 2)
	else
		if level >= a.max then return false end
		price = SealPrice(a, level + 1)
		if price == nil or SealsHeld(x) < price then return false end
	end
	local fields = "ability='" .. AbilityText(a.name) .. "',mod='" .. AbilityText(a.mod) .. "',refund=" .. (refund and "true" or "false") ..
		",previous=" .. level .. ",level=" .. (refund and level - 1 or level + 1) .. ",experience=" .. price .. ",player=" .. p
	if Refii._decide ~= nil and not Refii._decide(91, fields) then return false end
	if refund then Stats.ModifyGuildSeals(x, price) else Stats.SpendGuildSeals(x, price) end
	Refii.player_save(p, a.key, refund and level - 1 or level + 1)
	return true
end
function Refii.ability_buy(p, name) return Purchase(p, name, false) end
function Refii.ability_refund(p, name) return Purchase(p, name, true) end
-- What the engine states beside each level, for the players played here (mod_abilities.cpp State):
-- <key>.cost (-1 at the maximum or with no seal price), .refund, .afford. Run every 15th tick (B6).
function Refii._ability_state()
	for p = 1, 2 do
		local x = P(p)
		if x ~= nil and Refii.player_here(p) then
			local held = SealsHeld(x)
			for _, a in pairs(Refii._abilities) do
				local level = AbilityLevel(a, p)
				local next = level < a.max and SealPrice(a, level + 1) or nil
				local back = (a.half and level > 0) and SealPrice(a, level) or nil
				Refii.player_set(p, a.key .. ".cost", next or -1)
				Refii.player_set(p, a.key .. ".refund", back and math.floor(back / 2) or 0)
				Refii.player_set(p, a.key .. ".afford", (next ~= nil and held >= next) and 1 or 0)
			end
		end
	end
end
Refii._commands.ability_clear = { "_ability_clear", "" }
Refii._commands.ability_declare = { "_ability_declare", "ssnnssssss" }
Refii._commands.ability_buy = { "ability_buy", "ns" }
Refii._commands.ability_refund = { "ability_refund", "ns" }
StateAbilityCount()
""";

	/// <summary>Mounts the chunk at <see cref="Key"/>, owned by Refii, for the payload to read.</summary>
	public static void Attach(BnkFileSystem mounted)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(Source);
		mounted.AddVirtualFile(Key, Key, bytes.Length, () => bytes, "Refii");
	}
}
