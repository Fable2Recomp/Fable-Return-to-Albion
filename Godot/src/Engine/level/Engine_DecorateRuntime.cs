using System;
using System.Text;
using Refii.Bnk;
using Refii.Mods;

namespace Refii.Level;

public static class Engine_DecorateRuntime
{
    public const string Prefix = "refii.decorate.";
    public static void Attach(BnkFileSystem mounted, string catalog)
    {
        string previous = mounted.TryGetFile(ModLua.InitKey, out var init) ? Encoding.UTF8.GetString(init.ReadAllBytes()) : "";
        if (!previous.Contains("function Refii.on(", StringComparison.Ordinal)) previous = ModLua.RuntimePreamble() + previous;
        byte[] bytes = Encoding.UTF8.GetBytes(previous + "\n" + catalog + "\n" + Script);
        mounted.AddVirtualFile(ModLua.InitKey, ModLua.InitKey, bytes.Length, () => bytes, "Refii");
    }

    public const string Script = """
    if not RefiiDecorate then
        RefiiDecorate = {}
        local D = RefiiDecorate
        local saved = {version=1, nextid=1, levels={}}
        local building, house, level, selected, original, card, selectedEntry
        local active, revision, ticks, touched = false, 0, 0, 0
        local room, restored, restoring = {}, {}, {}
        local spawned = false
        local pending
        local currentLevel = ""
        local ready = false
        local function alive(e) return e and e:IsAlive() end
        local function finite(n) return type(n)=="number" and n==n and n~=math.huge and n~=-math.huge end
        local function publish(key, value) Refii.publish("refii.decorate." .. key, value) end
        local function uid(e) return alive(e) and Refii.uid(Refii.handle(e)) or "" end
        local function byuid(id)
            local h = id and Refii._by_uid(id)
            return h and Refii.entity(h) or nil
        end
        local function key(e) return string.lower(Refii.definition(Refii.handle(e)) or "") end
        local function current() return string.lower(Refii.level_name() or "") end
        local function entries()
            local list = saved.levels[level]
            if not list then list = {} saved.levels[level] = list end
            return list
        end
        local function position(e)
            local p, f = e:GetPosition(), Physics.GetFacingVector(e)
            return {x=p:GetX(), y=p:GetY(), z=p:GetZ(), yaw=math.atan2(f:GetX(), f:GetY())}
        end
        local function move(e, p)
            Physics.SetFacingVector(e,CVector3(math.sin(p.yaw),math.cos(p.yaw),0))
            Physics.TeleportToPosition(e,CVector3(p.x,p.y,p.z))
        end
        local function placed(e,p)
            if not alive(e) then return false end
            local actual=position(e)
            return (actual.x-p.x)^2+(actual.y-p.y)^2+(actual.z-p.z)^2<0.0004 and
                math.cos(actual.yaw-p.yaw)>0.999
        end
        local function movable(e)
            local definitions=RefiiDecorateMovable[key(e)]
            if not definitions then return nil end
            local value=#definitions==1 and 1 or UpgradeableFurniture.GetCurrentLevel(e)+1
            return definitions[value],value
        end
        local function setOwner(e,owner)
            if OwnedEntity.IsAvailable(e) then OwnedEntity.SetOwner(e,owner) end
        end
        local function replacement(item)
            if item.added then
                local inventory=RefiiDecorateCatalog[item.card]
                return inventory and inventory.definition
            end
            local definitions=item.sourceRecord and RefiiDecorateMovable[item.sourceRecord]
            return definitions and definitions[item.upgrade]
        end
        local function cancelRestoring()
            for _,stage in pairs(restoring) do
                if stage.created and alive(stage.entity) then stage.entity:Destroy() end
            end
            restoring={}
        end
        local function words(id, prefix)
            id = string.gsub(id or "", "^0x", "")
            id = string.rep("0", math.max(0,16-#id)) .. id
            for i=0,3 do publish(prefix .. i,tonumber(string.sub(id,13-i*4,16-i*4),16) or 0) end
        end
        local function state(status)
            publish("active",active and 1 or 0)
            publish("selected",(alive(selected) or card) and 1 or 0)
            publish("status",status or 0)
            words(uid(selected),"uid")
            words(card,"card")
            words(house,"house")
            words(string.format("%08x",alive(building) and Refii.handle(building) or 0),"building")
            if alive(selected) or card then
                local p=alive(selected) and position(selected) or original
                publish("x",p.x) publish("y",p.y) publish("z",p.z) publish("yaw",p.yaw)
            end
            revision=revision+1
            publish("revision",revision)
        end
        local function reject(reason)
            Refii._error("decorate placement failed: " .. reason)
            state(3)
        end
        local function belongs(e)
            if not alive(e) or not alive(building) then return false end
            local id=uid(e)
            for _,item in pairs(entries()) do if item.uid==id and item.house==house then return true end end
            if not RefiiDecorateFurniture[key(e)] then return false end
            if OwnedEntity.IsAvailable(e) then
                local owner=OwnedEntity.GetOwner(e)
                if alive(owner) then return owner==building end
            end
            return Whereabouts.IsAvailable(e) and Whereabouts.GetBuildingWeAreInsideOf(e)==building
        end
        function D.Refresh()
            room={}
            if not active then return end
            local search=SearchTools.StartNewSearch("object")
            SearchTools.FilterWithEC(search,Object.GetECType())
            local found=SearchTools.GetSearchResults(search)
            for _,e in ipairs(found) do
                if belongs(e) and e~=selected then
                    room[#room+1]=e
                    if #room>=1024 then break end
                end
            end
            publish("count",#room)
            for i,e in ipairs(room) do
                words(uid(e),"room" .. i .. ".uid")
                local definition=key(e)
                local itemCard
                for _,item in pairs(entries()) do if item.uid==uid(e) then itemCard=item.card break end end
                local item=itemCard and RefiiDecorateCatalog[itemCard]
                publish("room" .. i .. ".kind",item and item.kind or RefiiDecorateFurniture[definition] or 0)
            end
        end
        local function rollback()
            local wasPending=pending
            if spawned and alive(selected) then selected:Destroy() end
            if wasPending and not spawned and alive(selected) and original then move(selected,original) end
            if wasPending and wasPending.source then selected=wasPending.source
            elseif spawned then selected=nil end
            spawned=false pending=nil
        end
        function D.Cancel(quiet)
            rollback()
            selected, original, card, selectedEntry=nil,nil,nil,nil
            state(quiet and 0 or 6)
        end
        function D.End()
            D.Cancel(true)
            active=false
            if alive(building) and Refii._end_redecorating then Refii._end_redecorating(Refii.handle(building)) end
            building,house=nil,nil
            room={}
            state()
        end
        function D.Touch() touched=ticks end
        function D.Select(index)
            D.Cancel(true)
            local e=room[index]
            if not belongs(e) then state(1) return end
            selected=e original=position(e)
            for id,item in pairs(entries()) do if item.uid==uid(e) then selectedEntry=id break end end
            state()
        end
        function D.Add(id)
            D.Cancel(true)
            if saved.nextid>1000000 then state(3) return end
            local item=RefiiDecorateCatalog[id]
            local hero=GetPlayerHero()
            if not active or not item or not alive(hero) or (Refii.item_count(Refii.handle(hero),id) or 0)<1 then state(2) return end
            local p=hero:GetPosition()
            local f=Physics.GetFacingVector(hero)
            p=p+f*2
            card=id original={x=p:GetX(),y=p:GetY(),z=p:GetZ(),yaw=math.atan2(f:GetX(),f:GetY())}
            state()
        end
        function D.Commit(x,y,z,yaw)
            if pending then return end
            if not active or not alive(building) or (not alive(selected) and not card) then state(1) return end
            if not finite(x) or not finite(y) or not finite(z) or not finite(yaw) then reject("invalid position or rotation") return end
            local p={x=x,y=y,z=z,yaw=yaw}
            local request={pose=p,tick=ticks+2}
            if card then
                local hero=GetPlayerHero()
                local before=Refii.item_count(Refii.handle(hero),card) or 0
                if before<1 then D.Cancel() state(2) return end
                selected=Debug.CreateEntityAtPosition(RefiiDecorateCatalog[card].definition,"RefiiDecoratePlaced_" .. saved.nextid,CVector3(x,y,z))
                if not alive(selected) then selected=nil reject("could not spawn " .. RefiiDecorateCatalog[card].definition) return end
                spawned=true
                pending=request
                setOwner(selected,building)
            elseif not belongs(selected) then state(1) return
            elseif RefiiDecorateMovable[key(selected)] then
                local definition,upgrade=movable(selected)
                if not definition then reject("no movable definition for " .. key(selected) .. " at grade " .. tostring(upgrade)) return end
                if not selectedEntry and saved.nextid>1000000 then reject("saved furniture limit reached") return end
                request.source=selected request.sourceUid=uid(selected) request.sourceRecord=key(selected)
                request.definition=definition request.upgrade=upgrade
                local copy=Debug.CreateEntityAtPosition(definition,"RefiiDecoratePlaced_" .. (selectedEntry or saved.nextid),CVector3(x,y,z))
                if not alive(copy) then reject("could not spawn " .. definition .. " for " .. request.sourceUid) return end
                selected=copy spawned=true pending=request
                setOwner(selected,building)
            end
            pending=request
            move(selected,p)
        end
        function D.Finish()
            if not pending then return end
            local p=pending.pose
            if not placed(selected,p) then
                if alive(selected) then
                    local actual=position(selected)
                    Refii._error(string.format("decorate placement rejected %s: requested %.3f %.3f %.3f %.3f; actual %.3f %.3f %.3f %.3f",
                        uid(selected),p.x,p.y,p.z,p.yaw,actual.x,actual.y,actual.z,actual.yaw))
                else Refii._error("decorate placement failed: entity disappeared before confirmation") end
                rollback() state(3) return
            end
            local id=selectedEntry or tostring(saved.nextid)
            local item=selectedEntry and entries()[selectedEntry] or {}
            if card then
                local hero=GetPlayerHero()
                local before=alive(hero) and Refii.item_count(Refii.handle(hero),card) or 0
                if before<1 then D.Cancel() state(2) return end
                Inventory.RemoveItemOfType(hero,RefiiDecorateCatalog[card].inventory)
                if (Refii.item_count(Refii.handle(hero),card) or 0)~=before-1 then D.Cancel() state(2) return end
                item.card=card item.definition=RefiiDecorateCatalog[card].definition item.added=true
            end
            if pending.source then
                item.source=pending.sourceUid item.sourceRecord=pending.sourceRecord
                item.definition=pending.definition item.upgrade=pending.upgrade
                pending.source:Destroy()
            end
            item.uid=uid(selected) item.house=house
            item.record=key(selected)
            item.x=p.x item.y=p.y item.z=p.z item.yaw=p.yaw
            entries()[id]=item
            if not selectedEntry then saved.nextid=saved.nextid+1 end
            restored[id]=true
            spawned=false pending=nil
            selected,original,card,selectedEntry=nil,nil,nil,nil
            D.Refresh()
            state(5)
        end
        function D.Call(op,...)
            if op~="End" and op~="Touch" and (not active or current()~=level or not alive(building)) then return end
            touched=ticks
            local ok,err=pcall(D[op],...)
            if not ok then
                pcall(D.Cancel)
                state(3)
                Refii._error("decorate: " .. tostring(err))
            end
        end
        local function restore()
            local here=current()
            if here~=currentLevel then
                if active then D.End() end
                currentLevel=here restored={} restoring={}
            end
            local list=saved.levels[here]
            if not list then return end
            for id,item in pairs(list) do
                if not restored[id] and type(item)=="table" and finite(item.x) and finite(item.y) and finite(item.z) and finite(item.yaw) then
                    local stage=restoring[id]
                    if stage then
                        if placed(stage.entity,item) then
                            if alive(stage.source) then stage.source:Destroy() end
                            item.uid=uid(stage.entity) item.record=key(stage.entity)
                            if stage.sourceUid then
                                item.source=stage.sourceUid item.sourceRecord=stage.sourceRecord
                                item.definition=stage.definition item.upgrade=stage.upgrade
                            end
                            restored[id]=true
                        elseif stage.created and alive(stage.entity) then stage.entity:Destroy() end
                        restoring[id]=nil
                    else
                        local owner=byuid(item.house)
                        if alive(owner) then
                            local e=byuid(item.uid)
                            if alive(e) and item.record and key(e)~=item.record then e=nil end
                            local source=item.source and byuid(item.source)
                            if alive(source) and key(source)~=item.sourceRecord then source=nil end
                            local definition=replacement(item)
                            local upgrade
                            local created=false
                            if alive(e) and RefiiDecorateMovable[key(e)] then
                                source=e
                                definition,upgrade=movable(e)
                                e=nil
                            end
                            if not alive(e) and definition then
                                local name="RefiiDecoratePlaced_" .. id
                                local search=SearchTools.StartNewSearch("object")
                                SearchTools.FilterWithName(search,name)
                                e=SearchTools.GetSearchResults(search)[1]
                                if alive(e) and key(e)~=Refii.hash(definition) then e=nil end
                                if not alive(e) then
                                    e=Debug.CreateEntityAtPosition(definition,name,CVector3(item.x,item.y,item.z))
                                    created=alive(e)
                                end
                            end
                            if alive(e) then
                                local ok=pcall(function() setOwner(e,owner) move(e,item) end)
                                if ok then
                                    restoring[id]={entity=e,source=source,created=created,
                                        sourceUid=upgrade and uid(source),sourceRecord=upgrade and key(source),definition=definition,upgrade=upgrade}
                                elseif created then e:Destroy() end
                            end
                        end
                    end
                end
            end
        end
        Refii.on("redecorating",function(e)
            if e.player~=1 then return end
            if not e.on or e.on==0 then if active then D.End() end return end
            if (Refii.value("refii.decorate.enabled") or 0)<1 then return end
            if not Refii.world_here() then return end
            if active then D.End() end
            cancelRestoring()
            building=Refii.entity(e.building)
            if not alive(building) then return end
            house=uid(building) level=current()
            active=true touched=ticks
            D.Refresh() state()
        end)
        Refii.on("tick",function()
            ticks=ticks+1
            if active and (ticks-touched>150 or not alive(building) or current()~=level or (Refii.value("refii.decorate.enabled") or 0)<1) then D.End() end
            if pending and ticks>=pending.tick then D.Call("Finish") end
            if ticks%30==0 and ready and not active and Refii.world_here() and alive(GetPlayerHero()) then restore() end
        end)
        Refii.on("level_loaded",function() ready=true restored={} restoring={} end)
        Refii.on("level_unloading",function() D.End() ready=false restored={} restoring={} currentLevel="" end)
        Refii.on("game_saving",function() D.Cancel() cancelRestoring() end)
        local getSave,loadSave=GetSaveTable,LoadScriptSave
        GetSaveTable=function()
            D.Cancel()
            cancelRestoring()
            local result=getSave()
            result.RefiiDecorate=saved
            return result
        end
        LoadScriptSave=function(data)
            D.End()
            cancelRestoring()
            local value=data and data.RefiiDecorate
            saved=type(value)=="table" and value.version==1 and type(value.levels)=="table" and value or {version=1,nextid=1,levels={}}
            if not finite(saved.nextid) or saved.nextid<1 then saved.nextid=1 end
            for name,list in pairs(saved.levels) do
                if type(name)~="string" or type(list)~="table" then saved.levels[name]=nil
                else
                    for id,item in pairs(list) do
                        local number=tonumber(id)
                        if type(id)~="string" or not finite(number) or number<1 or number>1000000 or
                            type(item)~="table" or type(item.uid)~="string" or type(item.house)~="string" or
                            not finite(item.x) or not finite(item.y) or not finite(item.z) or not finite(item.yaw) then list[id]=nil
                        else saved.nextid=math.max(saved.nextid,number+1) end
                    end
                end
            end
            saved.nextid=math.min(1000001,math.floor(saved.nextid))
            ready=false restored={} restoring={} currentLevel=""
            return loadSave(data)
        end
        if PlutoPermanentsSaveTable then
            AddToPermanentsTables(GetSaveTable,"RefiiDecorateGetSave")
            AddToPermanentsTables(LoadScriptSave,"RefiiDecorateLoadSave")
            AddFunctionsInTableToPermanentsTables(D,"RefiiDecorateFunctions")
        end
        state()
    end
    """;
}
