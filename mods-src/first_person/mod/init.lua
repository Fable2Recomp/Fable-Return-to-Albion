-- First Person: the switch.
--
-- The camera itself is mod/camera.lua, which runs in the same VM each time the camera scripts
-- load. The two share the global FirstPerson table: this file owns FirstPerson.On, the camera
-- reads it every update.

FirstPerson = FirstPerson or {}
local F = FirstPerson

if F.On == nil then
	F.On = Refii.setting("first_person.start_on", 1) ~= 0
end

-- H (input.json "toggle", rebindable in Options > Controls). Player one only: split screen keeps
-- the game's shared camera, which frames both heroes.
Refii.on("input_action", function(e)
	if e.name == "first_person.toggle" and e.pressed and e.player == 1 then
		F.On = not F.On
		Refii.log("first person " .. (F.On and "on" or "off"))
	end
end)
